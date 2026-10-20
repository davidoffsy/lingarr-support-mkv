namespace Lingarr.Server.Services.Media;

/// <summary>
/// Normalizes stored video container extensions and decides whether MKV lookup is allowed.
/// </summary>
public static class MediaFileExtension
{
    /// <summary>
    /// Returns the lower-case extension of <paramref name="path"/> (including the leading dot),
    /// or <c>null</c> when the path has no usable extension.
    /// </summary>
    public static string? NormalizeFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        return extension.Length > 1 ? extension.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Unknown or empty extensions keep filesystem lookup for media synced before this
    /// metadata existed. Known non-MKV containers must not match a neighboring MKV.
    /// </summary>
    public static bool AllowsMkvLookup(string? fileExtension) =>
        string.IsNullOrWhiteSpace(fileExtension) ||
        string.Equals(fileExtension.Trim(), MediaFileLocator.MkvExtension, StringComparison.OrdinalIgnoreCase);
}
