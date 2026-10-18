using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services.Media;

/// <summary>
/// Languages that already have a full (non-forced) subtitle. Sidecars and every MKV
/// stream count, including image-based tracks that cannot be used as a translation source.
/// Forced-only tracks are signs/credits and do not mark the language as present.
/// </summary>
public static class TargetSubtitlePresence
{
    public static HashSet<string> FullLanguages(
        IEnumerable<Subtitles> sidecars,
        EmbeddedSubtitleProbeResult? probe = null)
    {
        ArgumentNullException.ThrowIfNull(sidecars);

        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sidecar in sidecars)
        {
            TryAdd(languages, sidecar.Language, sidecar.Caption);
        }

        if (probe is not { Found: true })
        {
            return languages;
        }

        foreach (var track in probe.Tracks)
        {
            TryAdd(languages, track.Language, track.Caption, track.IsForced);
        }

        foreach (var skipped in probe.Skipped)
        {
            TryAdd(languages, skipped.Language, skipped.Caption, skipped.IsForced);
        }

        return languages;
    }

    private static void TryAdd(
        ISet<string> languages,
        string? language,
        string? caption,
        bool embeddedForced = false)
    {
        if (string.IsNullOrWhiteSpace(language)
            || SourceLanguagePicker.IsUndetermined(language)
            || SubtitleCaptionFlags.IsForced(caption, embeddedForced))
        {
            return;
        }

        languages.Add(language.ToLowerInvariant());
    }
}
