using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Integration;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Models.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services.Plex;

public class PlexMediaResolver : IPlexMediaResolver
{
    private readonly ISettingService _settingService;
    private readonly IPlexApiClient _plexApiClient;
    private readonly PathConversionService _pathConversionService;
    private readonly LingarrDbContext _dbContext;
    private readonly IMediaService _mediaService;
    private readonly IRadarrService _radarrService;
    private readonly ISonarrService _sonarrService;
    private readonly ILogger<PlexMediaResolver> _logger;

    public PlexMediaResolver(
        ISettingService settingService,
        IPlexApiClient plexApiClient,
        PathConversionService pathConversionService,
        LingarrDbContext dbContext,
        IMediaService mediaService,
        IRadarrService radarrService,
        ISonarrService sonarrService,
        ILogger<PlexMediaResolver> logger)
    {
        _settingService = settingService;
        _plexApiClient = plexApiClient;
        _pathConversionService = pathConversionService;
        _dbContext = dbContext;
        _mediaService = mediaService;
        _radarrService = radarrService;
        _sonarrService = sonarrService;
        _logger = logger;
    }

    public async Task<PlexMediaResolveResult> ResolveAsync(
        PlexWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        var metadata = payload.Metadata;
        var ratingKey = metadata?.RatingKey;
        var title = metadata?.Title;
        var type = metadata?.Type;
        var webhookFile = PlexMediaFileSelector.SelectFile(metadata?.Media);
        var webhookGuids = metadata?.CollectGuids() ?? [];
        var parentIndex = metadata?.ParentIndex;
        var index = metadata?.Index;

        var plexUrl = await _settingService.GetSetting(SettingKeys.Integration.PlexUrl);
        var plexToken = await _settingService.GetEncryptedSetting(SettingKeys.Integration.PlexToken);
        var hasPlexCredentials = !string.IsNullOrWhiteSpace(plexUrl) && !string.IsNullOrWhiteSpace(plexToken);

        PlexMetadata? plexMetadata = null;
        if (hasPlexCredentials && !string.IsNullOrWhiteSpace(ratingKey))
        {
            plexMetadata = await _plexApiClient.GetMetadata(plexUrl!, plexToken!, ratingKey, cancellationToken);
        }

        var file = FirstNonEmpty(plexMetadata?.File, webhookFile);
        var itemGuids = plexMetadata?.Guids is { Count: > 0 }
            ? plexMetadata.Guids
            : webhookGuids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList();
        title = FirstNonEmpty(plexMetadata?.Title, title);
        type = FirstNonEmpty(plexMetadata?.Type, type);
        parentIndex = plexMetadata?.ParentIndex ?? parentIndex;
        index = plexMetadata?.Index ?? index;
        var year = plexMetadata?.Year ?? metadata?.Year;
        var grandparentTitle = FirstNonEmpty(plexMetadata?.GrandparentTitle, metadata?.GrandparentTitle);
        var grandparentRatingKey = FirstNonEmpty(
            plexMetadata?.GrandparentRatingKey,
            metadata?.GrandparentRatingKey);

        if (!hasPlexCredentials && string.IsNullOrEmpty(file))
        {
            _logger.LogInformation(
                "Plex media resolve skipped: Sign in with Plex required (ratingKey {RatingKey}, title {Title})",
                ratingKey,
                title);
            return PlexMediaResolveResult.Miss("Sign in with Plex required", ratingKey, title);
        }

        var isMovie = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);
        var mappingType = isMovie ? MediaType.Movie : MediaType.Show;
        var resultType = isMovie ? MediaType.Movie : MediaType.Episode;
        var displayTitle = isMovie
            ? PlexDisplayTitle.Movie(title, year)
            : PlexDisplayTitle.Episode(grandparentTitle, parentIndex, index, title);
        var externalIds = await ResolveLookupIdsAsync(
            isMovie,
            itemGuids,
            grandparentRatingKey,
            hasPlexCredentials,
            plexUrl,
            plexToken,
            cancellationToken);
        var hasLookupIds = isMovie ? externalIds.HasMovieIds : externalIds.HasShowIds;
        if (string.IsNullOrEmpty(file) && !hasLookupIds)
        {
            var reason = hasPlexCredentials && plexMetadata == null
                ? "Plex metadata not found"
                : "no file path available";
            _logger.LogInformation(
                "Plex media resolve skipped: {Reason} (ratingKey {RatingKey}, title {Title})",
                reason,
                ratingKey,
                title);
            return PlexMediaResolveResult.Miss(reason, ratingKey, title);
        }

        string? mappedPath = null;

        if (!string.IsNullOrEmpty(file))
        {
            mappedPath = _pathConversionService.ConvertAndMapPath(file, mappingType);
            var match = await MatchByPathAsync(resultType, mappedPath, cancellationToken);
            if (match != null)
            {
                return PlexMediaResolveResult.Hit(resultType, match.Id, mappedPath, ratingKey, displayTitle);
            }
        }

        var fromArr = await TryMatchByGuidAsync(
            resultType,
            externalIds,
            parentIndex,
            index);
        if (fromArr != 0)
        {
            return PlexMediaResolveResult.Hit(resultType, fromArr, mappedPath, ratingKey, displayTitle);
        }

        if (!string.IsNullOrEmpty(mappedPath))
        {
            _logger.LogInformation(
                "Plex media not in Lingarr library; using mapped path (ratingKey {RatingKey}, title {Title}, mapped path {MappedPath})",
                ratingKey,
                displayTitle,
                mappedPath);
            return PlexMediaResolveResult.PathHit(resultType, mappedPath, ratingKey, displayTitle);
        }

        _logger.LogInformation(
            "Plex media not in Lingarr library (ratingKey {RatingKey}, title {Title}, mapped path {MappedPath})",
            ratingKey,
            title,
            mappedPath);
        return PlexMediaResolveResult.Miss("not in Lingarr library", ratingKey, title, mappedPath);
    }

    private async Task<IMedia?> MatchByPathAsync(
        MediaType mediaType,
        string mappedPath,
        CancellationToken cancellationToken)
    {
        if (!TrySplitMappedFile(mappedPath, out var directory, out var stem))
        {
            return null;
        }

        if (mediaType == MediaType.Movie)
        {
            var movies = await _dbContext.Movies
                .Where(movie => movie.FileName == stem)
                .ToListAsync(cancellationToken);
            return movies.FirstOrDefault(movie => DirectoriesEqual(movie.Path, directory));
        }

        var episodes = await _dbContext.Episodes
            .Include(episode => episode.Season)
            .Where(episode => episode.FileName == stem)
            .ToListAsync(cancellationToken);
        return episodes.FirstOrDefault(episode =>
            DirectoriesEqual(episode.Path, directory) || DirectoriesEqual(episode.Season.Path, directory));
    }

    private async Task<int> TryMatchByGuidAsync(
        MediaType mediaType,
        PlexExternalIds ids,
        int? seasonNumber,
        int? episodeNumber)
    {
        if (mediaType == MediaType.Movie)
        {
            return await TryMatchMovieByGuidAsync(ids);
        }

        return await TryMatchEpisodeByGuidAsync(ids, seasonNumber, episodeNumber);
    }

    private async Task<PlexExternalIds> ResolveLookupIdsAsync(
        bool isMovie,
        IReadOnlyList<string> itemGuids,
        string? grandparentRatingKey,
        bool hasPlexCredentials,
        string? plexUrl,
        string? plexToken,
        CancellationToken cancellationToken)
    {
        if (isMovie)
        {
            return PlexGuidParser.Parse(itemGuids);
        }

        // Episode Guids are episode-level TVDB/IMDb/TMDB ids. Sonarr series lookup
        // needs the show ids from grandparent metadata.
        if (!hasPlexCredentials || string.IsNullOrWhiteSpace(grandparentRatingKey))
        {
            return default;
        }

        var showMetadata = await _plexApiClient.GetMetadata(
            plexUrl!,
            plexToken!,
            grandparentRatingKey,
            cancellationToken);
        return PlexGuidParser.Parse(showMetadata?.Guids);
    }

    private async Task<int> TryMatchMovieByGuidAsync(PlexExternalIds ids)
    {
        Models.Integrations.RadarrMovie? radarrMovie = null;
        if (int.TryParse(ids.TmdbId, out var tmdbId))
        {
            radarrMovie = await _radarrService.GetMovieByTmdbId(tmdbId);
        }

        if (radarrMovie == null && !string.IsNullOrEmpty(ids.ImdbId))
        {
            radarrMovie = await _radarrService.GetMovieByImdbId(ids.ImdbId);
        }

        if (radarrMovie == null)
        {
            return 0;
        }

        return await _mediaService.GetMovieIdOrSyncFromRadarrMovieId(radarrMovie.Id);
    }

    private async Task<int> TryMatchEpisodeByGuidAsync(
        PlexExternalIds ids,
        int? seasonNumber,
        int? episodeNumber)
    {
        if (!seasonNumber.HasValue || !episodeNumber.HasValue)
        {
            return 0;
        }

        Models.Integrations.SonarrShow? show = null;
        if (int.TryParse(ids.TvdbId, out var tvdbId))
        {
            show = await _sonarrService.GetShowByTvdbId(tvdbId);
        }

        if (show == null && !string.IsNullOrEmpty(ids.ImdbId))
        {
            show = await _sonarrService.GetShowByImdbId(ids.ImdbId);
        }

        if (show == null)
        {
            return 0;
        }

        var episodes = await _sonarrService.GetEpisodes(show.Id, seasonNumber.Value);
        var match = episodes?.FirstOrDefault(episode => episode.EpisodeNumber == episodeNumber.Value);
        if (match == null)
        {
            return 0;
        }

        return await _mediaService.GetEpisodeIdOrSyncFromSonarrEpisodeId(match.Id);
    }

    private bool TrySplitMappedFile(string mappedPath, out string directory, out string stem)
    {
        directory = string.Empty;
        stem = string.Empty;
        if (string.IsNullOrWhiteSpace(mappedPath))
        {
            return false;
        }

        var normalized = _pathConversionService.NormalizePath(mappedPath);
        if (!PlexMappedPath.TrySplit(normalized, out directory, out stem, out _))
        {
            return false;
        }

        directory = NormalizeDirectory(directory);
        return !string.IsNullOrEmpty(directory);
    }

    private bool DirectoriesEqual(string? left, string right)
    {
        return string.Equals(NormalizeDirectory(left), right, StringComparison.Ordinal);
    }

    private string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return _pathConversionService.NormalizePath(path).TrimEnd('/');
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }
}
