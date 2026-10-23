using System.Text.Json;
using System.Text.Json.Serialization;
using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Models.Webhooks;

public class PlexWebhookPayload
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("rating")]
    public JsonElement Rating { get; set; }

    [JsonPropertyName("owner")]
    public bool Owner { get; set; }

    [JsonPropertyName("user")]
    public bool User { get; set; }

    [JsonPropertyName("Account")]
    public PlexWebhookAccount? Account { get; set; }

    [JsonPropertyName("Metadata")]
    public PlexWebhookMetadata? Metadata { get; set; }

    [JsonPropertyName("Server")]
    public PlexWebhookServer? Server { get; set; }
}

public class PlexWebhookAccount
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public class PlexWebhookMetadata
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("ratingKey")]
    public string? RatingKey { get; set; }

    [JsonPropertyName("userRating")]
    public JsonElement UserRating { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("parentRatingKey")]
    public string? ParentRatingKey { get; set; }

    [JsonPropertyName("parentTitle")]
    public string? ParentTitle { get; set; }

    [JsonPropertyName("grandparentRatingKey")]
    public string? GrandparentRatingKey { get; set; }

    [JsonPropertyName("grandparentTitle")]
    public string? GrandparentTitle { get; set; }

    [JsonPropertyName("parentIndex")]
    public int? ParentIndex { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("guid")]
    public string? GuidString { get; set; }

    [JsonPropertyName("Guid")]
    public List<PlexGuid>? Guid { get; set; }

    [JsonPropertyName("Media")]
    public List<PlexMedia>? Media { get; set; }

    public IReadOnlyList<string> CollectGuids()
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(GuidString))
        {
            ids.Add(GuidString);
        }

        if (Guid == null)
        {
            return ids;
        }

        foreach (var item in Guid)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && !ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(item.Id);
            }
        }

        return ids;
    }
}

public class PlexWebhookServer
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}
