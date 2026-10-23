using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Interfaces.Services.Plex;

public interface IPlexSeasonEpisodeLister
{
    /// <summary>
    /// Lists episode metadata under a season rating key.
    /// Returns an empty list when the season is missing, empty, or Plex is not signed in.
    /// Throws on 401, 5xx, or timeout so Hangfire can retry.
    /// </summary>
    Task<IReadOnlyList<PlexMetadata>> ListEpisodesAsync(
        string seasonRatingKey,
        CancellationToken cancellationToken = default);
}
