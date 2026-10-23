using System.Text.Json;
using Lingarr.Server.Models.Webhooks;

namespace Lingarr.Server.Services.Plex;

internal static class PlexWebhookPayloadMapper
{
    public static PlexWebhookPayload Map(JsonElement root)
    {
        return new PlexWebhookPayload
        {
            Event = PlexJsonElement.GetString(root, "event"),
            Rating = PlexJsonElement.Clone(PlexJsonElement.GetProperty(root, "rating")),
            Owner = PlexJsonElement.GetBool(root, "owner"),
            User = PlexJsonElement.GetBool(root, "user"),
            Account = MapAccount(PlexJsonElement.GetProperty(root, "Account")),
            Metadata = MapMetadata(PlexJsonElement.GetProperty(root, "Metadata")),
            Server = MapServer(PlexJsonElement.GetProperty(root, "Server"))
        };
    }

    private static PlexWebhookAccount? MapAccount(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlexWebhookAccount
        {
            Id = PlexJsonElement.GetInt64(element, "id"),
            Title = PlexJsonElement.GetString(element, "title")
        };
    }

    private static PlexWebhookServer? MapServer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlexWebhookServer
        {
            Title = PlexJsonElement.GetString(element, "title"),
            Uuid = PlexJsonElement.GetString(element, "uuid")
        };
    }

    private static PlexWebhookMetadata? MapMetadata(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PlexWebhookMetadata
        {
            Type = PlexJsonElement.GetString(element, "type"),
            Title = PlexJsonElement.GetString(element, "title"),
            RatingKey = PlexJsonElement.GetString(element, "ratingKey"),
            UserRating = PlexJsonElement.Clone(PlexJsonElement.GetProperty(element, "userRating")),
            Year = PlexJsonElement.GetInt32(element, "year"),
            ParentRatingKey = PlexJsonElement.GetString(element, "parentRatingKey"),
            ParentTitle = PlexJsonElement.GetString(element, "parentTitle"),
            GrandparentRatingKey = PlexJsonElement.GetString(element, "grandparentRatingKey"),
            GrandparentTitle = PlexJsonElement.GetString(element, "grandparentTitle"),
            ParentIndex = PlexJsonElement.GetInt32(element, "parentIndex"),
            Index = PlexJsonElement.GetInt32(element, "index"),
            GuidString = PlexJsonElement.GetExactString(element, "guid"),
            Guid = PlexJsonElement.ReadGuidList(element),
            Media = PlexJsonElement.ReadMedia(element)
        };
    }
}
