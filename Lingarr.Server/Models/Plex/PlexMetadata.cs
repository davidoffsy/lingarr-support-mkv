namespace Lingarr.Server.Models.Plex;

public class PlexMetadata
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public string? RatingKey { get; init; }
    public string? ParentRatingKey { get; init; }
    public string? ParentTitle { get; init; }
    public string? GrandparentRatingKey { get; init; }
    public string? GrandparentTitle { get; init; }
    public int? ParentIndex { get; init; }
    public int? Index { get; init; }
    public string? File { get; init; }
    public IReadOnlyList<string> Guids { get; init; } = [];
    public IReadOnlyList<PlexSubtitleStream> SubtitleStreams { get; init; } = [];
}

public class PlexMedia
{
    public bool Selected { get; set; }
    public List<PlexPart>? Part { get; set; }
}

public class PlexPart
{
    public bool Selected { get; set; }
    public string? File { get; set; }
    public List<PlexSubtitleStream>? Stream { get; set; }
}

public class PlexGuid
{
    public string? Id { get; set; }
}
