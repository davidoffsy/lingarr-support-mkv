using Hangfire;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Services.Plex;

namespace Lingarr.Server.Jobs;

public class PlexRatingTriggerJob
{
    private readonly IPlexRatingTrigger _plexRatingTrigger;
    private readonly IPlexMediaResolver _plexMediaResolver;
    private readonly IPlexTranslationTargetFactory _plexTranslationTargetFactory;
    private readonly ILogger<PlexRatingTriggerJob> _logger;

    public PlexRatingTriggerJob(
        IPlexRatingTrigger plexRatingTrigger,
        IPlexMediaResolver plexMediaResolver,
        IPlexTranslationTargetFactory plexTranslationTargetFactory,
        ILogger<PlexRatingTriggerJob> logger)
    {
        _plexRatingTrigger = plexRatingTrigger;
        _plexMediaResolver = plexMediaResolver;
        _plexTranslationTargetFactory = plexTranslationTargetFactory;
        _logger = logger;
    }

    /// <summary>
    /// Probes sidecars/MKV and enqueues translation on the translation queue,
    /// so ffprobe cannot stall the webhook worker.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    [Queue("translation")]
    public Task Execute(
        PlexTranslationTarget target,
        CancellationToken cancellationToken = default)
    {
        return _plexRatingTrigger.EnqueueAsync(target, cancellationToken);
    }

    /// <summary>
    /// Resolves a Plex movie/episode on the translation queue, then selects sources.
    /// Webhook workers only expand seasons and enqueue this payload.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    [Queue("translation")]
    public async Task Execute(
        PlexRatingJobPayload payload,
        CancellationToken cancellationToken = default)
    {
        var resolved = await _plexMediaResolver.ResolveAsync(
            PlexRatingJobPayloadMapper.ToWebhook(payload),
            cancellationToken);
        if (!resolved.Found || resolved.MediaType is not MediaType mediaType)
        {
            _logger.LogInformation(
                "Plex webhook skipped: {Reason} (ratingKey {RatingKey}, title {Title})",
                resolved.SkipReason,
                payload.RatingKey,
                payload.Title);
            return;
        }

        var target = await _plexTranslationTargetFactory.TryCreateAsync(resolved, cancellationToken);
        if (target == null)
        {
            _logger.LogInformation(
                "Plex webhook skipped: could not build translation target (ratingKey {RatingKey}, title {Title}, path {MappedPath})",
                payload.RatingKey,
                payload.Title,
                resolved.MappedPath);
            return;
        }

        _logger.LogInformation(
            "Plex media resolved as {MediaType} mediaId {MediaId} (ratingKey {RatingKey}, path {MappedPath}, pathOnly {PathOnly})",
            mediaType,
            target.MediaId,
            payload.RatingKey,
            resolved.MappedPath,
            resolved.IsPathOnly);

        await _plexRatingTrigger.EnqueueAsync(target, cancellationToken);
    }

    /// <summary>
    /// Legacy Hangfire signature from before <see cref="PlexTranslationTarget"/>.
    /// Jobs already in the queue deserialize this overload.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    [Queue("translation")]
    public async Task Execute(
        MediaType mediaType,
        int entityId,
        string? ratingKey,
        CancellationToken cancellationToken = default)
    {
        var resolved = PlexMediaResolveResult.Hit(mediaType, entityId, null, ratingKey, null);
        var target = await _plexTranslationTargetFactory.TryCreateAsync(resolved, cancellationToken);
        if (target == null)
        {
            _logger.LogInformation(
                "Plex rating trigger skipped: library media {MediaType} {EntityId} is missing Path or FileName",
                mediaType,
                entityId);
            return;
        }

        await _plexRatingTrigger.EnqueueAsync(target, cancellationToken);
    }
}
