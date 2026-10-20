using Lingarr.Core.Interfaces;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Resolves a media item's container path from the stored directory + stem.
/// Phase 1 only recognizes <c>.mkv</c> files and does not query the database.
/// </summary>
public interface IMediaFileLocator
{
    /// <summary>
    /// Finds the MKV next to <paramref name="media"/> using <see cref="IMedia.Path"/> and
    /// <see cref="IMedia.FileName"/>. Known non-MKV <see cref="IMedia.FileExtension"/> values
    /// skip lookup so a neighboring MKV with the same stem is not selected. Unknown or empty
    /// extensions keep filesystem lookup for media synced before this metadata existed.
    /// </summary>
    /// <returns>The full path when an MKV exists; otherwise <c>null</c>.</returns>
    string? FindMkv(IMedia media);

    /// <summary>
    /// Finds <c>{directory}/{fileName}.mkv</c>, then falls back to files matching
    /// <c>{fileName}.*</c> with an <c>.mkv</c> extension. Does not consult
    /// <see cref="IMedia.FileExtension"/>; prefer <see cref="FindMkv(IMedia)"/> when the
    /// media entity is available.
    /// </summary>
    /// <returns>The full path when an MKV exists; otherwise <c>null</c>.</returns>
    string? FindMkv(string? directory, string? fileName);
}
