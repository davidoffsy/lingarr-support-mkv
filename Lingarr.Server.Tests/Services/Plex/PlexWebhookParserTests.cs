using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Server.Services.Plex;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexWebhookParserTests
{
    private readonly PlexWebhookParser _parser = new();

    [Fact]
    public void Parse_JsonMediaRate_ReadsMixedCasing()
    {
        const string json = """
            {
              "event": "media.rate",
              "rating": 10,
              "owner": true,
              "Account": { "id": 1, "title": "Owner" },
              "Metadata": { "type": "movie", "title": "Dune", "ratingKey": "42", "userRating": 10 }
            }
            """;

        var payload = _parser.Parse(json);

        Assert.NotNull(payload);
        Assert.Equal("media.rate", payload!.Event);
        Assert.True(payload.Owner);
        Assert.Equal(1, payload.Account?.Id);
        Assert.Equal("movie", payload.Metadata?.Type);
        Assert.Equal("42", payload.Metadata?.RatingKey);
        Assert.True(PlexRatingParser.TryGetRating(payload, out var rating));
        Assert.Equal(10, rating);
    }

    [Fact]
    public void Parse_SeasonMetadata_ReadsParentFields()
    {
        const string json = """
            {
              "event": "media.rate",
              "rating": 10,
              "Metadata": {
                "type": "season",
                "title": "Season 1",
                "ratingKey": "55",
                "parentRatingKey": "9",
                "parentTitle": "Orphan"
              }
            }
            """;

        var payload = _parser.Parse(json);

        Assert.NotNull(payload);
        Assert.Equal("season", payload!.Metadata?.Type);
        Assert.Equal("55", payload.Metadata?.RatingKey);
        Assert.Equal("9", payload.Metadata?.ParentRatingKey);
        Assert.Equal("Orphan", payload.Metadata?.ParentTitle);
    }

    [Theory]
    [InlineData("""{"event":"media.rate","rating":10.0,"Metadata":{"type":"movie"}}""")]
    [InlineData("""{"event":"media.rate","rating":"10","Metadata":{"type":"movie"}}""")]
    [InlineData("""{"event":"media.rate","Metadata":{"type":"movie","userRating":10}}""")]
    public void Parse_RatingVariants_AreAccepted(string json)
    {
        var payload = _parser.Parse(json);

        Assert.NotNull(payload);
        Assert.True(PlexRatingParser.TryGetRating(payload!, out var rating));
        Assert.Equal(10, rating);
    }

    [Fact]
    public async Task ReadPayloadJson_ApplicationJson_ReadsBody()
    {
        const string json = """{"event":"media.rate","rating":10}""";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var body = await _parser.ReadPayloadJsonAsync(context.Request);

        Assert.Equal(json, body);
        Assert.NotNull(_parser.Parse(body));
    }

    [Fact]
    public async Task ReadPayloadJson_Multipart_ReadsPayloadAndIgnoresJpeg()
    {
        const string json = """{"event":"media.rate","rating":10,"Metadata":{"type":"movie"}}""";
        const string boundary = "----plexboundary";
        var body =
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"payload\"\r\n\r\n" +
            json + "\r\n" +
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"thumb\"; filename=\"thumb.jpg\"\r\n" +
            "Content-Type: image/jpeg\r\n\r\n" +
            "jpeg-bytes-not-json\r\n" +
            $"--{boundary}--\r\n";

        var context = new DefaultHttpContext();
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var payloadJson = await _parser.ReadPayloadJsonAsync(context.Request);

        Assert.Equal(json, payloadJson);
        var payload = _parser.Parse(payloadJson);
        Assert.NotNull(payload);
        Assert.Equal("media.rate", payload!.Event);
    }

    [Fact]
    public async Task ReadPayloadJson_OversizedJson_ReturnsNull()
    {
        var json = "{\"event\":\"media.rate\",\"padding\":\"" + new string('x', PlexWebhookParser.MaxPayloadBytes) + "\"}";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = json.Length;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var body = await _parser.ReadPayloadJsonAsync(context.Request);

        Assert.Null(body);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        Assert.Null(_parser.Parse("{not-json"));
        Assert.Null(_parser.Parse(null));
        Assert.Null(_parser.Parse(""));
    }

    [Fact]
    public void Parse_OfficialGuidString_DoesNotFail()
    {
        const string json = """
            {
              "event": "media.rate",
              "user": true,
              "owner": true,
              "rating": 10,
              "Account": { "id": 1, "title": "wf2022" },
              "Metadata": {
                "type": "movie",
                "title": "Hope",
                "ratingKey": 1936545,
                "guid": "plex://movie/5d776b9a9ab54400214e65b2",
                "userRating": 10,
                "year": 2013
              }
            }
            """;

        var payload = _parser.Parse(json);

        Assert.NotNull(payload);
        Assert.Equal("media.rate", payload!.Event);
        Assert.Equal("1936545", payload.Metadata?.RatingKey);
        Assert.Equal("Hope", payload.Metadata?.Title);
        Assert.Contains("plex://movie/5d776b9a9ab54400214e65b2", payload.Metadata!.CollectGuids());
        Assert.True(PlexRatingParser.TryGetRating(payload, out var rating));
        Assert.Equal(10, rating);
    }

    [Fact]
    public void Parse_GuidStringAndGuidArray_Together()
    {
        const string json = """
            {
              "event": "media.rate",
              "rating": 10,
              "Account": { "id": 1, "title": "wf2022" },
              "Metadata": {
                "type": "movie",
                "title": "Dune",
                "ratingKey": "42",
                "guid": "plex://movie/abc",
                "Guid": [ { "id": "tmdb://12345" }, { "id": "imdb://tt123" } ]
              }
            }
            """;

        var payload = _parser.Parse(json);

        Assert.NotNull(payload);
        var guids = payload!.Metadata!.CollectGuids();
        Assert.Contains("plex://movie/abc", guids);
        Assert.Contains("tmdb://12345", guids);
        Assert.Contains("imdb://tt123", guids);
    }

    [Fact]
    public void Parse_SingleMediaObject_ReadsFile()
    {
        const string json = """
            {
              "event": "media.rate",
              "rating": 10,
              "Metadata": {
                "type": "movie",
                "title": "Dune",
                "ratingKey": "42",
                "Media": { "Part": { "file": "/movies/Dune.mkv" } }
              }
            }
            """;

        var payload = _parser.Parse(json);

        Assert.NotNull(payload);
        Assert.Equal("/movies/Dune.mkv", PlexMediaFileSelector.SelectFile(payload!.Metadata?.Media));
    }
}
