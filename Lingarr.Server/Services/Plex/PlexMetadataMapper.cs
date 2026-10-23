using System.Text.Json;
using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Services.Plex;

public static class PlexMetadataMapper
{
    public static PlexMetadata FromJson(JsonElement item)
    {
        var media = PlexJsonElement.ReadMedia(item);
        return new PlexMetadata
        {
            Type = PlexJsonElement.GetString(item, "type"),
            Title = PlexJsonElement.GetString(item, "title"),
            Year = PlexJsonElement.GetInt32(item, "year"),
            RatingKey = PlexJsonElement.GetString(item, "ratingKey"),
            ParentRatingKey = PlexJsonElement.GetString(item, "parentRatingKey"),
            ParentTitle = PlexJsonElement.GetString(item, "parentTitle"),
            GrandparentRatingKey = PlexJsonElement.GetString(item, "grandparentRatingKey"),
            GrandparentTitle = PlexJsonElement.GetString(item, "grandparentTitle"),
            ParentIndex = PlexJsonElement.GetInt32(item, "parentIndex"),
            Index = PlexJsonElement.GetInt32(item, "index"),
            File = PlexMediaFileSelector.SelectFile(media),
            Guids = PlexJsonElement.CollectGuidIds(item),
            SubtitleStreams = PlexMediaFileSelector.SelectSubtitleStreams(media)
        };
    }

    public static JsonElement? FirstMetadataItem(JsonElement root)
    {
        var container = PlexJsonElement.GetProperty(root, "MediaContainer");
        var metadata = PlexJsonElement.GetProperty(container, "Metadata");
        if (metadata.ValueKind == JsonValueKind.Array)
        {
            return metadata.GetArrayLength() == 0 ? null : metadata[0];
        }

        return metadata.ValueKind == JsonValueKind.Object ? metadata : null;
    }

    public static IEnumerable<JsonElement> AllMetadataItems(JsonElement root)
    {
        var container = PlexJsonElement.GetProperty(root, "MediaContainer");
        var metadata = PlexJsonElement.GetProperty(container, "Metadata");
        if (metadata.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in metadata.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            yield return metadata;
        }
    }

    public static PlexChildrenPage ReadChildrenPage(JsonElement root)
    {
        var container = PlexJsonElement.GetProperty(root, "MediaContainer");
        var items = new List<PlexMetadata>();
        foreach (var item in AllMetadataItems(root))
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                items.Add(FromJson(item));
            }
        }

        return new PlexChildrenPage
        {
            Items = items,
            Size = PlexJsonElement.GetInt32(container, "size") ?? items.Count,
            TotalSize = PlexJsonElement.GetInt32(container, "totalSize") ?? 0,
            Offset = PlexJsonElement.GetInt32(container, "offset") ?? 0
        };
    }
}
