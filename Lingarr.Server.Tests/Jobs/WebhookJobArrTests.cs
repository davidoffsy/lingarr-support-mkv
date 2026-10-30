using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Jobs;
using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

public class WebhookJobArrTests
{
    private readonly LingarrDbContext _db;
    private readonly Mock<IMediaService> _media = new();
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly WebhookJob _job;

    public WebhookJobArrTests()
    {
        _db = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase($"arr-webhook-{Guid.NewGuid()}").Options);
        _jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-id");
        _job = new WebhookJob(_db, _media.Object,
            Mock.Of<IPlexWebhookParser>(), Mock.Of<IPlexRaterFilter>(),
            Mock.Of<IPlexSeasonEpisodeLister>(),
            _jobs.Object,
            _settings.Object, NullLogger<WebhookJob>.Instance);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task RadarrDownload_SyncsAndOnlyTranslatesWhenEnabled(string setting, bool translate)
    {
        var movie = new Movie { RadarrId = 12, Title = "Movie", Path = "/movies", FileName = "movie.mkv", DateAdded = DateTimeOffset.UtcNow };
        _db.Movies.Add(movie);
        await _db.SaveChangesAsync();
        _media.Setup(m => m.GetMovieIdOrSyncFromRadarrMovieId(12)).ReturnsAsync(movie.Id);
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.RadarrWebhookAutoTranslate)).ReturnsAsync(setting);

        await _job.ProcessRadarrWebhook(new RadarrWebhookPayload
        {
            EventType = "Download", Movie = new RadarrWebhookMovie { Id = 12, Title = "Movie" }
        });

        _media.Verify(m => m.GetMovieIdOrSyncFromRadarrMovieId(12), Times.Once);
        VerifyArrTranslationSchedule(movie.Id, MediaType.Movie, translate ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task SonarrDownload_SyncsEveryEpisodeAndOnlyTranslatesWhenEnabled(string setting, bool translate)
    {
        var show = new Show { SonarrId = 5, Title = "Show", Path = "/shows", DateAdded = DateTimeOffset.UtcNow };
        var season = new Season { SeasonNumber = 1, Show = show };
        var episodes = new[] { 21, 22 }.Select(id => new Episode
        {
            SonarrId = id, EpisodeNumber = id, Title = $"Episode {id}", Season = season
        }).ToArray();
        _db.Episodes.AddRange(episodes);
        await _db.SaveChangesAsync();
        foreach (var episode in episodes)
            _media.Setup(m => m.GetEpisodeIdOrSyncFromSonarrEpisodeId(episode.SonarrId)).ReturnsAsync(episode.Id);
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.SonarrWebhookAutoTranslate)).ReturnsAsync(setting);

        await _job.ProcessSonarrWebhook(new SonarrWebhookPayload
        {
            EventType = "Download", Series = new SonarrWebhookSeries { Id = 5, Title = "Show" },
            Episodes = episodes.Select(e => new SonarrWebhookEpisode { Id = e.SonarrId }).ToList()
        });

        foreach (var episode in episodes)
        {
            _media.Verify(m => m.GetEpisodeIdOrSyncFromSonarrEpisodeId(episode.SonarrId), Times.Once);
            VerifyArrTranslationSchedule(episode.Id, MediaType.Episode, translate ? Times.Once() : Times.Never());
        }
    }

    [Fact]
    public async Task AutoTranslateToggles_AreIndependentPerApp()
    {
        var movie = new Movie { RadarrId = 12, Title = "Movie", Path = "/movies", FileName = "movie.mkv", DateAdded = DateTimeOffset.UtcNow };
        var show = new Show { SonarrId = 5, Title = "Show", Path = "/shows", DateAdded = DateTimeOffset.UtcNow };
        var season = new Season { SeasonNumber = 1, Show = show };
        var episode = new Episode { SonarrId = 21, EpisodeNumber = 1, Title = "Episode 1", Season = season };
        _db.Movies.Add(movie);
        _db.Episodes.Add(episode);
        await _db.SaveChangesAsync();
        _media.Setup(m => m.GetMovieIdOrSyncFromRadarrMovieId(12)).ReturnsAsync(movie.Id);
        _media.Setup(m => m.GetEpisodeIdOrSyncFromSonarrEpisodeId(21)).ReturnsAsync(episode.Id);
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.RadarrWebhookAutoTranslate)).ReturnsAsync("false");
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.SonarrWebhookAutoTranslate)).ReturnsAsync("true");

        await _job.ProcessRadarrWebhook(new RadarrWebhookPayload
        {
            EventType = "Download", Movie = new RadarrWebhookMovie { Id = 12, Title = "Movie" }
        });
        await _job.ProcessSonarrWebhook(new SonarrWebhookPayload
        {
            EventType = "Download",
            Series = new SonarrWebhookSeries { Id = 5, Title = "Show" },
            Episodes = new[] { new SonarrWebhookEpisode { Id = 21 } }.ToList()
        });

        VerifyArrTranslationSchedule(movie.Id, MediaType.Movie, Times.Never());
        VerifyArrTranslationSchedule(episode.Id, MediaType.Episode, Times.Once());
    }

    [Fact]
    public async Task MissingAppSetting_FallsBackToLegacyCombinedToggle()
    {
        var movie = new Movie { RadarrId = 12, Title = "Movie", Path = "/movies", FileName = "movie.mkv", DateAdded = DateTimeOffset.UtcNow };
        _db.Movies.Add(movie);
        await _db.SaveChangesAsync();
        _media.Setup(m => m.GetMovieIdOrSyncFromRadarrMovieId(12)).ReturnsAsync(movie.Id);
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.RadarrWebhookAutoTranslate)).ReturnsAsync((string?)null);
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.ArrWebhookAutoTranslate)).ReturnsAsync("false");

        await _job.ProcessRadarrWebhook(new RadarrWebhookPayload
        {
            EventType = "Download", Movie = new RadarrWebhookMovie { Id = 12, Title = "Movie" }
        });

        VerifyArrTranslationSchedule(movie.Id, MediaType.Movie, Times.Never());
    }

    [Fact]
    public async Task NonDownloadEvent_DoesNotSyncOrTranslate()
    {
        await _job.ProcessRadarrWebhook(new RadarrWebhookPayload
        {
            EventType = "Rename", Movie = new RadarrWebhookMovie { Id = 12 }
        });

        _media.Verify(m => m.GetMovieIdOrSyncFromRadarrMovieId(It.IsAny<int>()), Times.Never);
        _jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    private void VerifyArrTranslationSchedule(int mediaId, MediaType mediaType, Times times)
    {
        _jobs.Verify(
            c => c.Create(
                It.Is<Job>(job =>
                    job.Type == typeof(ArrWebhookTranslationJob)
                    && (int)job.Args[0]! == mediaId
                    && (MediaType)job.Args[1]! == mediaType
                    && (int)job.Args[2]! == 0),
                It.Is<IState>(state => state is ScheduledState)),
            times);
    }
}
