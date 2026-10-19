using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services.Media;

/// <summary>
/// Languages that already have a full (non-forced) subtitle. Sidecars and every MKV
/// stream count, including image-based tracks that cannot be used as a translation source.
/// Forced-only tracks are signs/credits and do not mark the language as present.
/// SDH / HI / CC tracks are full dialogue and do count. Presence matching uses
/// <see cref="LanguageCodeService.GetBestMatch"/> so <c>zh-CN</c> matches a <c>zh</c> track.
/// </summary>
public static class TargetSubtitlePresence
{
    private static readonly LanguageCodeService LanguageCodes = new();

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

    /// <summary>
    /// True when <paramref name="targetLanguage"/> already has a full subtitle,
    /// including region/script variants (e.g. <c>zh-CN</c> vs <c>zh</c>).
    /// </summary>
    public static bool Covers(IEnumerable<string> presentLanguages, string? targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(presentLanguages);

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return false;
        }

        if (presentLanguages is ISet<string> set && set.Contains(targetLanguage))
        {
            return true;
        }

        var present = presentLanguages as ICollection<string> ?? presentLanguages.ToList();
        if (present.Count == 0)
        {
            return false;
        }

        if (present is not ISet<string>
            && present.Any(language => string.Equals(language, targetLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return LanguageCodes.GetBestMatch(targetLanguage, present) != null;
    }

    public static bool CoversAll(
        IEnumerable<string> presentLanguages,
        IEnumerable<string> targetLanguages)
    {
        ArgumentNullException.ThrowIfNull(presentLanguages);
        ArgumentNullException.ThrowIfNull(targetLanguages);

        var present = presentLanguages as ICollection<string> ?? presentLanguages.ToList();
        var any = false;
        foreach (var target in targetLanguages)
        {
            any = true;
            if (!Covers(present, target))
            {
                return false;
            }
        }

        return any;
    }

    public static List<string> Missing(
        IEnumerable<string> presentLanguages,
        IEnumerable<string> targetLanguages)
    {
        ArgumentNullException.ThrowIfNull(presentLanguages);
        ArgumentNullException.ThrowIfNull(targetLanguages);

        var present = presentLanguages as ICollection<string> ?? presentLanguages.ToList();
        return targetLanguages.Where(language => !Covers(present, language)).ToList();
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
