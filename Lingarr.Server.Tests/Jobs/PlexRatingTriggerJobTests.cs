using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Jobs;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Models.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

public class PlexRatingTriggerJobTests
{
    private readonly Mock<IPlexRatingTrigger> _trigger = new();
    private readonly Mock<IPlexMediaResolver> _resolver = new();
    private readonly Mock<IPlexTranslationTargetFactory> _factory = new();
    private readonly PlexRatingTriggerJob _job;

    public PlexRatingTriggerJobTests()
    {
        _job = new PlexRatingTriggerJob(
            _trigger.Object,
            _resolver.Object,
            _factory.Object,
            NullLogger<PlexRatingTriggerJob>.Instance);
    }

    [Fact]
    public async Task Execute_Payload_ResolvesAndEnqueuesTarget()
    {
        var payload = new PlexRatingJobPayload
        {
            Type = "movie",
            Title = "Foo",
            RatingKey = "1",
            File = "/mnt/plex/Foo.mkv"
        };
        var resolved = PlexMediaResolveResult.Hit(MediaType.Movie, 9, "/movies/Foo/Foo.mkv", "1", "Foo");
        var target = new PlexTranslationTarget
        {
            MediaType = MediaType.Movie,
            MediaId = 9,
            Directory = "/movies/Foo",
            FileName = "Foo",
            RatingKey = "1"
        };
        _resolver.Setup(r => r.ResolveAsync(It.Is<PlexWebhookPayload>(p => p.Metadata!.RatingKey == "1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _factory.Setup(f => f.TryCreateAsync(resolved, It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);

        await _job.Execute(payload);

        _trigger.Verify(t => t.EnqueueAsync(target, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_PayloadMiss_DoesNotEnqueue()
    {
        var payload = new PlexRatingJobPayload { Type = "movie", RatingKey = "1", Title = "Foo" };
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<PlexWebhookPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlexMediaResolveResult.Miss("not in Lingarr library", "1", "Foo"));

        await _job.Execute(payload);

        _factory.Verify(
            f => f.TryCreateAsync(It.IsAny<PlexMediaResolveResult>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _trigger.Verify(
            t => t.EnqueueAsync(It.IsAny<PlexTranslationTarget>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_LegacyLibraryHit_BuildsTargetFromFactory()
    {
        var resolved = PlexMediaResolveResult.Hit(MediaType.Movie, 9, null, "42", null);
        var target = new PlexTranslationTarget
        {
            MediaType = MediaType.Movie,
            MediaId = 9,
            Directory = "/movies/Foo",
            FileName = "Foo",
            RatingKey = "42"
        };
        _factory.Setup(f => f.TryCreateAsync(
                It.Is<PlexMediaResolveResult>(r => r.EntityId == 9 && !r.IsPathOnly),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);

        await _job.Execute(MediaType.Movie, 9, "42");

        _resolver.Verify(
            r => r.ResolveAsync(It.IsAny<PlexWebhookPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _trigger.Verify(t => t.EnqueueAsync(target, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_LegacyMissingMedia_DoesNotEnqueue()
    {
        _factory.Setup(f => f.TryCreateAsync(It.IsAny<PlexMediaResolveResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlexTranslationTarget?)null);

        await _job.Execute(MediaType.Movie, 9, "42");

        _trigger.Verify(
            t => t.EnqueueAsync(It.IsAny<PlexTranslationTarget>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_PayloadResolveThrows_DoesNotSwallow()
    {
        var payload = new PlexRatingJobPayload { Type = "episode", RatingKey = "102", Title = "Two" };
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<PlexWebhookPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Plex metadata 502"));

        await Assert.ThrowsAsync<HttpRequestException>(() => _job.Execute(payload));

        _trigger.Verify(
            t => t.EnqueueAsync(It.IsAny<PlexTranslationTarget>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
