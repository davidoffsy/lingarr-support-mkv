using Lingarr.Core.Enum;

namespace Lingarr.Server.Models.Plex;

public class PlexMediaResolveResult
{
    public bool Found { get; init; }
    public bool IsPathOnly { get; init; }
    public MediaType? MediaType { get; init; }
    public int EntityId { get; init; }
    public string? MappedPath { get; init; }
    public string? SkipReason { get; init; }
    public string? RatingKey { get; init; }
    public string? Title { get; init; }

    public static PlexMediaResolveResult Miss(
        string reason,
        string? ratingKey = null,
        string? title = null,
        string? mappedPath = null) =>
        new()
        {
            Found = false,
            SkipReason = reason,
            RatingKey = ratingKey,
            Title = title,
            MappedPath = mappedPath
        };

    public static PlexMediaResolveResult Hit(
        MediaType mediaType,
        int entityId,
        string? mappedPath,
        string? ratingKey,
        string? title) =>
        new()
        {
            Found = true,
            MediaType = mediaType,
            EntityId = entityId,
            MappedPath = mappedPath,
            RatingKey = ratingKey,
            Title = title
        };

    public static PlexMediaResolveResult PathHit(
        MediaType mediaType,
        string mappedPath,
        string? ratingKey,
        string? title) =>
        new()
        {
            Found = true,
            IsPathOnly = true,
            MediaType = mediaType,
            MappedPath = mappedPath,
            RatingKey = ratingKey,
            Title = title
        };
}
