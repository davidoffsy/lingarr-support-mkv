using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Services.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexApiClientChildrenTests
{
    [Fact]
    public async Task GetChildren_ParsesEpisodesAndSendsContainerHeaders()
    {
        const string json = """
            {
              "MediaContainer": {
                "size": 2,
                "totalSize": 2,
                "offset": 0,
                "Metadata": [
                  {
                    "ratingKey": "101",
                    "type": "episode",
                    "title": "One",
                    "index": 1,
                    "parentIndex": 1,
                    "grandparentTitle": "Orphan",
                    "grandparentRatingKey": "9",
                    "Media": { "Part": { "file": "/shows/Orphan/S01E01.mkv" } }
                  },
                  {
                    "ratingKey": "102",
                    "type": "episode",
                    "title": "Two",
                    "index": 2,
                    "parentIndex": 1,
                    "grandparentTitle": "Orphan",
                    "Media": { "Part": { "file": "/shows/Orphan/S01E02.mkv" } }
                  }
                ]
              }
            }
            """;
        var handler = new StubHandler { Status = HttpStatusCode.OK, Body = json };
        var client = CreateClient(handler);

        var page = await client.GetChildren("http://plex:32400", "sign-in-token", "55", 0, 200);

        Assert.NotNull(page);
        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(2, page.TotalSize);
        Assert.Equal("101", page.Items[0].RatingKey);
        Assert.Equal("episode", page.Items[0].Type);
        Assert.Equal("/shows/Orphan/S01E01.mkv", page.Items[0].File);
        Assert.Equal("Orphan", page.Items[0].GrandparentTitle);
        Assert.Equal("/library/metadata/55/children", handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Equal("sign-in-token", handler.LastRequest?.Headers.GetValues("X-Plex-Token").First());
        Assert.Equal("0", handler.LastRequest?.Headers.GetValues("X-Plex-Container-Start").First());
        Assert.Equal("200", handler.LastRequest?.Headers.GetValues("X-Plex-Container-Size").First());
    }

    [Fact]
    public async Task GetChildren_NotFound_ReturnsNull()
    {
        var handler = new StubHandler { Status = HttpStatusCode.NotFound, Body = "{}" };
        var client = CreateClient(handler);

        var page = await client.GetChildren("http://plex:32400", "token", "missing");

        Assert.Null(page);
    }

    [Fact]
    public async Task GetChildren_Unauthorized_Throws()
    {
        var handler = new StubHandler { Status = HttpStatusCode.Unauthorized, Body = "{}" };
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetChildren("http://plex:32400", "bad-token", "55"));
    }

    [Fact]
    public async Task GetChildren_Timeout_Throws()
    {
        var handler = new StubHandler { Throw = new TaskCanceledException("timed out") };
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetChildren("http://plex:32400", "token", "55"));
    }

    private static IPlexApiClient CreateClient(StubHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Plex"))
            .Returns(() => new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(10)
            });
        return new PlexApiClient(factory.Object, NullLogger<PlexApiClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "{}";
        public Exception? Throw { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (Throw != null)
            {
                throw Throw;
            }

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
