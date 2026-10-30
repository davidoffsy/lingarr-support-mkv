using System.Globalization;
using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Services.Plex;
using Lingarr.Server.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Jobs;

public class WebhookJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly IMediaService _mediaService;
    private readonly IPlexWebhookParser _plexWebhookParser;
    private readonly IPlexRaterFilter _plexRaterFilter;
    private readonly IPlexSeasonEpisodeLister _plexSeasonEpisodeLister;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ISettingService _settingService;
    private readonly ILogger<WebhookJob> _logger;

    public WebhookJob(
        LingarrDbContext dbContext,
        IMediaService mediaService,
        IPlexWebhookParser plexWebhookParser,
        IPlexRaterFilter plexRaterFilter,
        IPlexSeasonEpisodeLister plexSeasonEpisodeLister,
        IBackgroundJobClient backgroundJobClient,
        ISettingService settingService,
        ILogger<WebhookJob> logger)
    {
        _dbContext = dbContext;
        _mediaService = mediaService;
        _plexWebhookParser = plexWebhookParser;
        _plexRaterFilter = plexRaterFilter;
        _plexSeasonEpisodeLister = plexSeasonEpisodeLister;
        _backgroundJobClient = backgroundJobClient;
        _settingService = settingService;
        _logger = logger;
    }

    private async Task<bool> AutoTranslateEnabled(string key)
    {
        var value = await _settingService.GetSetting(key);
        if (string.IsNullOrEmpty(value))
        {
            value = await _settingService.GetSetting(SettingKeys.Integration.ArrWebhookAutoTranslate);
        }

        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60)]
    [AutomaticRetry(Attempts = 3)]
    [Queue("webhook")]
    public async Task ProcessRadarrWebhook(RadarrWebhookPayload payload)
    {
        if (ArrWebhookPayloadFilter.ShouldIgnoreRadarr(payload, out var reason))
        {
            _logger.LogInformation("Radarr webhook skipped: {Reason}", reason);
            return;
        }

        try
        {
            var movieId = await _mediaService.GetMovieIdOrSyncFromRadarrMovieId(payload.Movie!.Id);
            if (movieId == 0)
            {
                _logger.LogWarning("Failed to sync or find movie with Radarr ID {RadarrId}. Movie may not have a file yet.",
                    payload.Movie.Id);
                return;
            }
            
            var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == movieId);
            if (movie == null)
            {
                _logger.LogError("Movie with ID {MovieId} not found in database after sync", movieId);
                return;
            }

            if (!await AutoTranslateEnabled(SettingKeys.Integration.RadarrWebhookAutoTranslate)) return;
            ScheduleArrTranslation(movieId, MediaType.Movie);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Radarr webhook for movie {MovieTitle} (Radarr ID: {RadarrId})",
                payload.Movie!.Title, payload.Movie.Id);
            throw;
        }
    }

    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60)]
    [AutomaticRetry(Attempts = 3)]
    [Queue("webhook")]
    public async Task ProcessSonarrWebhook(SonarrWebhookPayload payload)
    {
        if (ArrWebhookPayloadFilter.ShouldIgnoreSonarr(payload, out var reason))
        {
            _logger.LogInformation("Sonarr webhook skipped: {Reason}", reason);
            return;
        }

        try
        {
            var autoTranslate = await AutoTranslateEnabled(SettingKeys.Integration.SonarrWebhookAutoTranslate);
            foreach (var episode in payload.Episodes!)
            {
                var episodeId = await _mediaService.GetEpisodeIdOrSyncFromSonarrEpisodeId(episode.Id);
                if (episodeId == 0)
                {
                    _logger.LogWarning("Failed to sync or find episode with Sonarr ID {SonarrEpisodeId}. Episode may not have a file yet.",
                        episode.Id);
                    continue;
                }
                
                var episodeEntity = await _dbContext.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId);
                if (episodeEntity == null)
                {
                    _logger.LogError("Episode with ID {EpisodeId} not found in database after sync", episodeId);
                    continue;
                }

                if (autoTranslate)
                {
                    ScheduleArrTranslation(episodeId, MediaType.Episode);
                }
            }

            _logger.LogInformation("Completed processing Sonarr webhook for series: {SeriesTitle}", payload.Series!.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Sonarr webhook for series {SeriesTitle} (Sonarr ID: {SonarrId})",
                payload.Series!.Title, payload.Series.Id);
            throw;
        }
    }

    [DisableConcurrentExecution(timeoutInSeconds: 2 * 60)]
    [AutomaticRetry(Attempts = 3)]
    [Queue("webhook")]
    public async Task ProcessPlexWebhook(string payloadJson)
    {
        var payload = _plexWebhookParser.Parse(payloadJson);
        if (payload == null)
        {
            _logger.LogInformation("Plex webhook skipped: invalid payload");
            return;
        }

        var minRaw = await _settingService.GetSetting(SettingKeys.Integration.PlexMinRating);
        if (!decimal.TryParse(minRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var minRating))
        {
            minRating = 10;
        }

        if (PlexWebhookEventFilter.ShouldSkip(payload, minRating, out var reason))
        {
            _logger.LogInformation("Plex webhook skipped: {Reason}", reason);
            return;
        }

        if (!await _plexRaterFilter.IsAllowedAsync(payload.Account))
        {
            _logger.LogInformation(
                "Plex webhook skipped: account {AccountId} ({AccountTitle}) is not allowed to trigger translations",
                payload.Account?.Id,
                payload.Account?.Title);
            return;
        }

        if (IsSeason(payload))
        {
            await EnqueueSeasonEpisodes(payload);
            return;
        }

        EnqueueRatingJob(PlexRatingJobPayloadMapper.FromWebhook(payload));
    }

    private async Task EnqueueSeasonEpisodes(PlexWebhookPayload payload)
    {
        var seasonKey = payload.Metadata?.RatingKey;
        if (string.IsNullOrWhiteSpace(seasonKey))
        {
            _logger.LogInformation(
                "Plex season webhook skipped: missing ratingKey (title {Title})",
                payload.Metadata?.Title);
            return;
        }

        var episodes = await _plexSeasonEpisodeLister.ListEpisodesAsync(seasonKey);
        if (episodes.Count == 0)
        {
            _logger.LogInformation(
                "Plex season webhook finished with no episodes (ratingKey {RatingKey}, title {Title})",
                seasonKey,
                payload.Metadata?.Title);
            return;
        }

        var enqueued = 0;
        foreach (var episode in episodes)
        {
            if (string.IsNullOrWhiteSpace(episode.RatingKey))
            {
                continue;
            }

            EnqueueRatingJob(PlexRatingJobPayloadMapper.FromEpisode(episode));
            enqueued++;
        }

        _logger.LogInformation(
            "Plex season expanded to {Count} translation jobs (season ratingKey {RatingKey}, title {Title})",
            enqueued,
            seasonKey,
            payload.Metadata?.Title);
    }

    private void EnqueueRatingJob(PlexRatingJobPayload payload)
    {
        _backgroundJobClient.Enqueue<PlexRatingTriggerJob>(job =>
            job.Execute(payload, CancellationToken.None));
    }

    private void ScheduleArrTranslation(int mediaId, MediaType mediaType)
    {
        _backgroundJobClient.Schedule<ArrWebhookTranslationJob>(
            job => job.Execute(mediaId, mediaType, 0, CancellationToken.None),
            ArrWebhookTranslationRetry.InitialDelay);
    }

    private static bool IsSeason(PlexWebhookPayload payload) =>
        string.Equals(payload.Metadata?.Type, "season", StringComparison.OrdinalIgnoreCase);
}
