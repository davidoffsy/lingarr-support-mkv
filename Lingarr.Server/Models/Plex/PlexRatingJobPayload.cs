namespace Lingarr.Server.Models.Plex;

/// <summary>
/// Hangfire-serializable Plex webhook item. Avoids <see cref="System.Text.Json.JsonElement"/>
/// on <see cref="Models.Webhooks.PlexWebhookPayload"/>, which Hangfire cannot round-trip.
/// </summary>
public sealed class PlexRatingJobPayload
{
    public string? Type { get; set; }

    public string? Title { get; set; }

    public string? RatingKey { get; set; }

    public int? Year { get; set; }

    public string? GrandparentRatingKey { get; set; }

    public string? GrandparentTitle { get; set; }

    public string? ParentTitle { get; set; }

    public int? ParentIndex { get; set; }

    public int? Index { get; set; }

    public List<string> Guids { get; set; } = [];

    public string? File { get; set; }
}
