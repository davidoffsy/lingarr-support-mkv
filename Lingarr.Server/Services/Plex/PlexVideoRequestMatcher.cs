using Lingarr.Server.Services.Media;

namespace Lingarr.Server.Services.Plex;

/// <summary>
/// Decides whether a translation request's subtitle path belongs to the same
/// video (directory + stem) as a Plex rating target.
/// </summary>
public static class PlexVideoRequestMatcher
{
    private static readonly LanguageCodeService LanguageCodes = new();

    private static readonly HashSet<string> CaptionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "sdh",
        "cc",
        "forced",
        "hi",
        "default"
    };

    public static bool IsSameVideo(string? subtitleToTranslate, string directory, string stem)
    {
        if (string.IsNullOrWhiteSpace(subtitleToTranslate) ||
            string.IsNullOrEmpty(directory) ||
            string.IsNullOrEmpty(stem))
        {
            return false;
        }

        var filePath = EmbeddedSubtitlePlaceholder.TryParse(subtitleToTranslate, out var mkvPath, out _)
            ? mkvPath
            : subtitleToTranslate;

        if (!PlexMappedPath.TrySplit(filePath, out var fileDirectory, out var fileStem, out _))
        {
            return false;
        }

        var expectedDirectory = directory.Replace('\\', '/').TrimEnd('/');
        if (!string.Equals(fileDirectory, expectedDirectory, StringComparison.Ordinal))
        {
            return false;
        }

        return StemMatches(fileStem, stem);
    }

    /// <summary>
    /// Accepts the video stem, <c>{stem}.{lang}</c>, and caption suffixes such as
    /// <c>{stem}.{lang}.forced</c>. Rejects a longer title that only shares a prefix
    /// (<c>The</c> vs <c>The.Office</c>).
    /// </summary>
    private static bool StemMatches(string fileStem, string stem)
    {
        if (string.Equals(fileStem, stem, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = stem + ".";
        if (!fileStem.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = fileStem[prefix.Length..];
        if (string.IsNullOrEmpty(suffix))
        {
            return false;
        }

        foreach (var part in suffix.Split('.'))
        {
            if (CaptionTags.Contains(part) ||
                LanguageCodes.IsUnknown(part) ||
                LanguageCodes.Validate(part))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
