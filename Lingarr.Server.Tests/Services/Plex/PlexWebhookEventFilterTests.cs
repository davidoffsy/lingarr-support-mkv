using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Services.Plex;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexWebhookEventFilterTests
{
    private readonly PlexWebhookParser _parser = new();

    [Fact]
    public void ShouldSkip_MediaPlay_IsTrue()
    {
        var payload = Parse("""{"event":"media.play","rating":10,"Metadata":{"type":"movie"}}""");

        Assert.True(PlexWebhookEventFilter.ShouldSkip(payload, 10, out var reason));
        Assert.Contains("media.play", reason);
    }

    [Fact]
    public void ShouldSkip_RatingSix_IsTrue()
    {
        var payload = Parse("""{"event":"media.rate","rating":6,"Metadata":{"type":"movie"}}""");

        Assert.True(PlexWebhookEventFilter.ShouldSkip(payload, 10, out var reason));
        Assert.Contains("below minimum", reason);
    }

    [Fact]
    public void ShouldSkip_TypeShow_IsTrue()
    {
        var payload = Parse("""{"event":"media.rate","rating":10,"Metadata":{"type":"show"}}""");

        Assert.True(PlexWebhookEventFilter.ShouldSkip(payload, 10, out var reason));
        Assert.Contains("show", reason);
    }

    [Fact]
    public void ShouldSkip_TypeSeason_IsFalse()
    {
        var payload = Parse("""{"event":"media.rate","rating":10,"Metadata":{"type":"season","title":"Season 1"}}""");

        Assert.False(PlexWebhookEventFilter.ShouldSkip(payload, 10, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ShouldSkip_MediaRateMovieTen_IsFalse()
    {
        var payload = Parse("""{"event":"media.rate","rating":10,"Metadata":{"type":"movie","title":"Dune"}}""");

        Assert.False(PlexWebhookEventFilter.ShouldSkip(payload, 10, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    private PlexWebhookPayload Parse(string json)
    {
        var payload = _parser.Parse(json);
        Assert.NotNull(payload);
        return payload!;
    }
}
