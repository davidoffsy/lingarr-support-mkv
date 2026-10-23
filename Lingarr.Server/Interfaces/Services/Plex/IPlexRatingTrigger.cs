using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Interfaces.Services.Plex;

public interface IPlexRatingTrigger
{
    /// <summary>
    /// Probes sidecars + MKV text tracks, ranks a Plex source, and enqueues translation
    /// requests. Never extracts in the webhook worker.
    /// </summary>
    Task EnqueueAsync(PlexTranslationTarget target, CancellationToken cancellationToken = default);
}
