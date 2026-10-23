using Lingarr.Server.Models.Webhooks;

namespace Lingarr.Server.Services.Plex;

public static class PlexWebhookEventFilter
{
    public static bool ShouldSkip(PlexWebhookPayload payload, decimal minRating, out string reason)
    {
        if (!string.Equals(payload.Event, "media.rate", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"event '{payload.Event}' is not media.rate";
            return true;
        }

        if (!PlexRatingParser.TryGetRating(payload, out var rating))
        {
            reason = $"missing or below minimum rating {minRating}";
            return true;
        }

        if (rating < minRating)
        {
            reason = $"rating {rating} is below minimum {minRating}";
            return true;
        }

        var type = payload.Metadata?.Type;
        if (!IsSupportedType(type))
        {
            reason = $"metadata type '{type}' is not movie, episode, or season";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsSupportedType(string? type) =>
        string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "season", StringComparison.OrdinalIgnoreCase);
}
