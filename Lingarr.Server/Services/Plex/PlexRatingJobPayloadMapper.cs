using Lingarr.Server.Models.Plex;
using Lingarr.Server.Models.Webhooks;

namespace Lingarr.Server.Services.Plex;

internal static class PlexRatingJobPayloadMapper
{
    public static PlexRatingJobPayload FromWebhook(PlexWebhookPayload payload)
    {
        var metadata = payload.Metadata;
        return new PlexRatingJobPayload
        {
            Type = metadata?.Type,
            Title = metadata?.Title,
            RatingKey = metadata?.RatingKey,
            Year = metadata?.Year,
            GrandparentRatingKey = metadata?.GrandparentRatingKey,
            GrandparentTitle = metadata?.GrandparentTitle,
            ParentTitle = metadata?.ParentTitle,
            ParentIndex = metadata?.ParentIndex,
            Index = metadata?.Index,
            Guids = metadata?.CollectGuids().ToList() ?? [],
            File = PlexMediaFileSelector.SelectFile(metadata?.Media)
        };
    }

    public static PlexRatingJobPayload FromEpisode(PlexMetadata episode) =>
        new()
        {
            Type = "episode",
            Title = episode.Title,
            RatingKey = episode.RatingKey,
            Year = episode.Year,
            GrandparentRatingKey = episode.GrandparentRatingKey,
            GrandparentTitle = episode.GrandparentTitle,
            ParentIndex = episode.ParentIndex,
            Index = episode.Index,
            Guids = episode.Guids.ToList(),
            File = episode.File
        };

    public static PlexWebhookPayload ToWebhook(PlexRatingJobPayload payload) =>
        new()
        {
            Event = "media.rate",
            Metadata = new PlexWebhookMetadata
            {
                Type = payload.Type,
                Title = payload.Title,
                RatingKey = payload.RatingKey,
                Year = payload.Year,
                GrandparentRatingKey = payload.GrandparentRatingKey,
                GrandparentTitle = payload.GrandparentTitle,
                ParentTitle = payload.ParentTitle,
                ParentIndex = payload.ParentIndex,
                Index = payload.Index,
                Guid = payload.Guids.Count == 0
                    ? null
                    : payload.Guids.Select(id => new PlexGuid { Id = id }).ToList(),
                Media = string.IsNullOrEmpty(payload.File)
                    ? null
                    :
                    [
                        new PlexMedia
                        {
                            Selected = true,
                            Part = [new PlexPart { Selected = true, File = payload.File }]
                        }
                    ]
            }
        };
}
