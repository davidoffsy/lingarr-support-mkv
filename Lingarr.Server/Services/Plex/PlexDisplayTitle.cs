namespace Lingarr.Server.Services.Plex;

public static class PlexDisplayTitle
{
    public static string Movie(string? title, int? year)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Unknown Movie" : title.Trim();
        if (year is > 0)
        {
            var suffix = $"({year.Value})";
            if (!name.Contains(suffix, StringComparison.Ordinal))
            {
                return $"{name} {suffix}";
            }
        }

        return name;
    }

    public static string Episode(
        string? showTitle,
        int? seasonNumber,
        int? episodeNumber,
        string? episodeTitle)
    {
        var episode = string.IsNullOrWhiteSpace(episodeTitle) ? "Unknown Episode" : episodeTitle.Trim();
        if (string.IsNullOrWhiteSpace(showTitle))
        {
            return episode;
        }

        var show = showTitle.Trim();
        if (!seasonNumber.HasValue || !episodeNumber.HasValue)
        {
            return $"{show} - {episode}";
        }

        return $"{show} - S{seasonNumber.Value:D2}E{episodeNumber.Value:D2} - {episode}";
    }
}
