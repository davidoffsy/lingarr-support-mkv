namespace Lingarr.Server.Services.Plex;

public static class PlexMappedPath
{
    public static bool TrySplit(
        string? mappedPath,
        out string directory,
        out string stem,
        out string? extension)
    {
        directory = string.Empty;
        stem = string.Empty;
        extension = null;
        if (string.IsNullOrWhiteSpace(mappedPath))
        {
            return false;
        }

        var normalized = mappedPath.Replace('\\', '/').Trim();
        var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/').TrimEnd('/');
        stem = Path.GetFileNameWithoutExtension(normalized) ?? string.Empty;
        var ext = Path.GetExtension(normalized);
        extension = ext.Length > 1 ? ext.ToLowerInvariant() : null;
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem))
        {
            return false;
        }

        directory = dir;
        return true;
    }
}
