using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Jobs;

public class ArrWebhookTranslationJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly IMediaSubtitleProcessor _mediaSubtitleProcessor;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<ArrWebhookTranslationJob> _logger;

    public ArrWebhookTranslationJob(
        LingarrDbContext dbContext,
        IMediaSubtitleProcessor mediaSubtitleProcessor,
        IBackgroundJobClient backgroundJobClient,
        ILogger<ArrWebhookTranslationJob> logger)
    {
        _dbContext = dbContext;
        _mediaSubtitleProcessor = mediaSubtitleProcessor;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    /// <summary>
    /// Scans one Arr movie/episode after the webhook sync. File-not-ready is
    /// retried on this job's schedule, not via Hangfire exception retries.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [Queue("translation")]
    public async Task Execute(
        int mediaId,
        MediaType mediaType,
        int attempt,
        CancellationToken cancellationToken)
    {
        var media = await LoadMedia(mediaId, mediaType, cancellationToken);
        if (media == null)
        {
            _logger.LogWarning(
                "Arr webhook translation skipped: {MediaType} {MediaId} was not found (attempt {Attempt})",
                mediaType,
                mediaId,
                attempt);
            return;
        }

        var result = await _mediaSubtitleProcessor.ProcessMediaDetailed(media, mediaType);
        if (!result.Retryable)
        {
            return;
        }

        var delay = ArrWebhookTranslationRetry.NextDelay(attempt);
        if (delay == null)
        {
            _logger.LogInformation(
                "Arr webhook translation giving up after {Attempts} attempts for {MediaType} {MediaId} ({FileName})",
                attempt + 1,
                mediaType,
                mediaId,
                media.FileName);
            return;
        }

        _logger.LogInformation(
            "Arr webhook translation incomplete for {MediaType} {MediaId} ({FileName}); retry {NextAttempt} in {Delay}",
            mediaType,
            mediaId,
            media.FileName,
            attempt + 1,
            delay);
        _backgroundJobClient.Schedule<ArrWebhookTranslationJob>(
            job => job.Execute(mediaId, mediaType, attempt + 1, CancellationToken.None),
            delay.Value);
    }

    private async Task<IMedia?> LoadMedia(
        int mediaId,
        MediaType mediaType,
        CancellationToken cancellationToken) =>
        mediaType switch
        {
            MediaType.Movie => await _dbContext.Movies
                .FirstOrDefaultAsync(movie => movie.Id == mediaId, cancellationToken),
            MediaType.Episode => await _dbContext.Episodes
                .FirstOrDefaultAsync(episode => episode.Id == mediaId, cancellationToken),
            _ => null
        };
}
