using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Jobs;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Services.Plex;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

public class WebhookJobPlexTests
{
    private readonly Mock<IMediaService> _media = new();
    private readonly Mock<IMediaSubtitleProcessor> _processor = new();
    private readonly Mock<IPlexRaterFilter> _rater = new();
    private readonly Mock<IPlexSeasonEpisodeLister> _seasons = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly Mock<ISettingService> _settings = new();
    private readonly PlexWebhookParser _parser = new();
    private readonly WebhookJob _job;

    public WebhookJobPlexTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase("plex-webhook-job-" + System.Guid.NewGuid())
            .Options;
        var db = new LingarrDbContext(options);
        _jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-id");
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.PlexMinRating))
            .ReturnsAsync("10");
        _rater.Setup(r => r.IsAllowedAsync(It.IsAny<PlexWebhookAccount?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _seasons.Setup(l => l.ListEpisodesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _job = new WebhookJob(
            db,
            _media.Object,
            _processor.Object,
            _parser,
            _rater.Object,
            _seasons.Object,
            _jobs.Object,
            _settings.Object,
            NullLogger<WebhookJob>.Instance);
    }

    [Fact]
    public async Task ProcessPlexWebhook_MediaPlay_DoesNotAskRater()
    {
        await _job.ProcessPlexWebhook("""{"event":"media.play","rating":10,"Metadata":{"type":"movie"}}""");

        _rater.Verify(
            r => r.IsAllowedAsync(It.IsAny<PlexWebhookAccount?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _processor.Verify(
            p => p.ProcessMedia(It.IsAny<Lingarr.Core.Interfaces.IMedia>(), It.IsAny<MediaType>()),
            Times.Never);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPlexWebhook_LowRating_DoesNotAskRater()
    {
        await _job.ProcessPlexWebhook("""{"event":"media.rate","rating":6,"Metadata":{"type":"movie"}}""");

        _rater.Verify(
            r => r.IsAllowedAsync(It.IsAny<PlexWebhookAccount?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPlexWebhook_TypeShow_DoesNotAskRater()
    {
        await _job.ProcessPlexWebhook("""{"event":"media.rate","rating":10,"Metadata":{"type":"show"}}""");

        _rater.Verify(
            r => r.IsAllowedAsync(It.IsAny<PlexWebhookAccount?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPlexWebhook_PassingFilters_EnqueuesPayloadWithoutResolving()
    {
        await _job.ProcessPlexWebhook(
            """{"event":"media.rate","rating":10,"Account":{"id":1,"title":"Owner"},"Metadata":{"type":"movie","ratingKey":"1","title":"Foo"}}""");

        _rater.Verify(
            r => r.IsAllowedAsync(It.IsAny<PlexWebhookAccount?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _processor.Verify(
            p => p.ProcessMedia(It.IsAny<Lingarr.Core.Interfaces.IMedia>(), It.IsAny<MediaType>()),
            Times.Never);
        _jobs.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(PlexRatingTriggerJob)
                    && job.Args[0].GetType() == typeof(PlexRatingJobPayload)
                    && ((PlexRatingJobPayload)job.Args[0]).Type == "movie"
                    && ((PlexRatingJobPayload)job.Args[0]).RatingKey == "1"
                    && ((PlexRatingJobPayload)job.Args[0]).Title == "Foo"),
                It.IsAny<IState>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessPlexWebhook_TypeSeason_ExpandsEpisodesIntoJobs()
    {
        _seasons.Setup(l => l.ListEpisodesAsync("55", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Episode("101", "One", 1),
                Episode("102", "Two", 2),
                Episode("103", "Three", 3)
            ]);

        await _job.ProcessPlexWebhook(
            """{"event":"media.rate","rating":10,"Account":{"id":1,"title":"Owner"},"Metadata":{"type":"season","ratingKey":"55","title":"Season 1","parentTitle":"Orphan"}}""");

        _seasons.Verify(l => l.ListEpisodesAsync("55", It.IsAny<CancellationToken>()), Times.Once);
        _jobs.Verify(c => c.Create(It.Is<Job>(job => job.Type == typeof(PlexRatingTriggerJob)), It.IsAny<IState>()),
            Times.Exactly(3));
        _jobs.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(PlexRatingTriggerJob)
                    && ((PlexRatingJobPayload)job.Args[0]).RatingKey == "101"
                    && ((PlexRatingJobPayload)job.Args[0]).Type == "episode"
                    && ((PlexRatingJobPayload)job.Args[0]).File == "/mnt/plex/Orphan/S01E01.mkv"),
                It.IsAny<IState>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessPlexWebhook_TypeSeasonEmpty_DoesNotEnqueue()
    {
        await _job.ProcessPlexWebhook(
            """{"event":"media.rate","rating":10,"Account":{"id":1,"title":"Owner"},"Metadata":{"type":"season","ratingKey":"55","title":"Season 1"}}""");

        _seasons.Verify(l => l.ListEpisodesAsync("55", It.IsAny<CancellationToken>()), Times.Once);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPlexWebhook_TypeSeason_SkipsEpisodeWithoutRatingKey()
    {
        _seasons.Setup(l => l.ListEpisodesAsync("55", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Episode("", "One", 1),
                Episode("102", "Two", 2)
            ]);

        await _job.ProcessPlexWebhook(
            """{"event":"media.rate","rating":10,"Account":{"id":1,"title":"Owner"},"Metadata":{"type":"season","ratingKey":"55","title":"Season 1"}}""");

        _jobs.Verify(c => c.Create(It.Is<Job>(job => job.Type == typeof(PlexRatingTriggerJob)), It.IsAny<IState>()),
            Times.Once);
        _jobs.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(PlexRatingTriggerJob)
                    && ((PlexRatingJobPayload)job.Args[0]).RatingKey == "102"),
                It.IsAny<IState>()),
            Times.Once);
    }

    private static PlexMetadata Episode(string ratingKey, string title, int index) =>
        new()
        {
            Type = "episode",
            RatingKey = ratingKey,
            Title = title,
            ParentIndex = 1,
            Index = index,
            GrandparentTitle = "Orphan",
            File = $"/mnt/plex/Orphan/S01E{index:00}.mkv"
        };
}
