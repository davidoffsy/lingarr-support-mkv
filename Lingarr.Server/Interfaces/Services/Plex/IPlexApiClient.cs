using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Interfaces.Services.Plex;

public interface IPlexApiClient
{
    Task<PlexIdentity?> GetIdentity(
        string baseUrl,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches PMS metadata for a rating key.
    /// Returns <c>null</c> on 404 or when the item has no metadata.
    /// Throws on 401, 5xx, or timeout so Hangfire can retry.
    /// </summary>
    Task<PlexMetadata?> GetMetadata(
        string baseUrl,
        string accessToken,
        string ratingKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches one page of PMS children for a rating key (season episodes).
    /// Returns <c>null</c> on 404. Throws on 401, 5xx, or timeout so Hangfire can retry.
    /// </summary>
    Task<PlexChildrenPage?> GetChildren(
        string baseUrl,
        string accessToken,
        string ratingKey,
        int start = 0,
        int size = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a PMS subtitle stream (OpenSubtitles sideload, etc.).
    /// Returns <c>null</c> on 404 or when the body is empty / too large.
    /// Throws on 401, 5xx, or timeout so Hangfire can retry.
    /// </summary>
    Task<byte[]?> DownloadStream(
        string baseUrl,
        string accessToken,
        string streamKey,
        CancellationToken cancellationToken = default);
}
