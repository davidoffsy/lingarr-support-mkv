using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Services.Media;
using Lingarr.Server.Services.Plex;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexPathRatingTriggerTests : IDisposable
{
    private const string Directory = "/other/Unknown";
    private const string Stem = "Unknown";
    private const string MkvPath = "/other/Unknown/Unknown.mkv";
    private const string SidecarPath = "/other/Unknown/Unknown.en.srt";

    private readonly LingarrDbContext _db;
    private readonly Mock<ISubtitleService> _subtitles = new();
    private readonly Mock<IMediaFileLocator> _locator = new();
    private readonly Mock<IEmbeddedSubtitleService> _embedded = new();
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<ITranslationRequestService> _requests = new();
    private readonly Mock<IPlexSubtitleImporter> _importer = new();
    private readonly PlexRatingTrigger _trigger;

    public PlexPathRatingTriggerTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase("plex-path-rating-trigger-" + Guid.NewGuid())
            .Options;
        _db = new LingarrDbContext(options);

        _settings.Setup(s => s.GetSettingAsJson<SourceLanguage>(SettingKeys.Translation.SourceLanguages))
            .ReturnsAsync(new List<SourceLanguage> { new() { Code = "en", Name = "English" } });
        _settings.Setup(s => s.GetSettingAsJson<TargetLanguage>(SettingKeys.Translation.TargetLanguages))
            .ReturnsAsync(new List<TargetLanguage> { new() { Code = "zh", Name = "Chinese" } });
        _settings.Setup(s => s.GetSetting(SettingKeys.Translation.IgnoreCaptions))
            .ReturnsAsync("false");

        _locator.Setup(l => l.FindMkv(Directory, Stem)).Returns(MkvPath);
        _subtitles.Setup(s => s.GetSubtitles(Directory, Stem))
            .ReturnsAsync(new List<Subtitles>
            {
                new()
                {
                    Path = SidecarPath,
                    FileName = "Unknown.en",
                    Language = "en",
                    Caption = "",
                    Format = ".srt"
                }
            });
        _embedded.Setup(s => s.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.Success([], []));
        _requests.Setup(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()))
            .ReturnsAsync(1);
        _importer.Setup(i => i.ListAsync(
                It.IsAny<string?>(),
                It.IsAny<MediaType>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlexSourceSubtitleCandidate>());

        _trigger = new PlexRatingTrigger(
            _db,
            _subtitles.Object,
            _locator.Object,
            _embedded.Object,
            _settings.Object,
            _requests.Object,
            _importer.Object,
            NullLogger<PlexRatingTrigger>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Enqueue_PathOnlyMovie_CreatesRequestWithoutMediaId()
    {
        await _trigger.EnqueueAsync(PathTarget());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.MediaId == null
                && t.Title == "Unknown (2020)"
                && t.MediaType == MediaType.Movie
                && t.SubtitlePath == SidecarPath
                && t.TargetLanguage == "zh")),
            Times.Once);
        Assert.Empty(_db.Movies);
    }

    [Fact]
    public async Task Enqueue_PathOnlyCompletedRequest_DoesNotCreateRequest()
    {
        _db.TranslationRequests.Add(new TranslationRequest
        {
            MediaId = null,
            Title = "Unknown (2020)",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            SubtitleToTranslate = SidecarPath,
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed
        });
        await _db.SaveChangesAsync();

        await _trigger.EnqueueAsync(PathTarget());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        Assert.Empty(_db.Movies);
    }

    [Fact]
    public async Task Enqueue_EmptyDirectory_DoesNotCreateRequest()
    {
        await _trigger.EnqueueAsync(new PlexTranslationTarget
        {
            MediaType = MediaType.Movie,
            DisplayTitle = "Missing",
            RatingKey = "1"
        });

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        _subtitles.Verify(
            s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task Enqueue_LibraryHit_StillPassesMediaId()
    {
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Foo",
            Path = "/movies/Foo",
            FileName = "Foo",
            DateAdded = DateTimeOffset.UtcNow
        };
        _db.Movies.Add(movie);
        await _db.SaveChangesAsync();
        _subtitles.Setup(s => s.GetSubtitles("/movies/Foo", "Foo"))
            .ReturnsAsync(new List<Subtitles>
            {
                new()
                {
                    Path = "/movies/Foo/Foo.en.srt",
                    FileName = "Foo.en",
                    Language = "en",
                    Caption = "",
                    Format = ".srt"
                }
            });
        _locator.Setup(l => l.FindMkv("/movies/Foo", "Foo")).Returns("/movies/Foo/Foo.mkv");

        await _trigger.EnqueueAsync(new PlexTranslationTarget
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            Directory = "/movies/Foo",
            FileName = "Foo",
            FileExtension = ".mkv",
            DisplayTitle = "Foo",
            RatingKey = "42",
            MappedFilePath = "/movies/Foo/Foo.mkv"
        });

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t => t.MediaId == movie.Id)),
            Times.Once);
        Assert.Equal(1, _db.Movies.Count());
    }

    [Fact]
    public async Task Enqueue_CompletedEpisode_DoesNotBlockOtherEpisodeInSeason()
    {
        const string showDir = "/shows/Orphan";
        SetupEpisodeSource(showDir, "S01E01");
        SetupEpisodeSource(showDir, "S01E02");
        _db.TranslationRequests.Add(new TranslationRequest
        {
            MediaId = null,
            Title = "Orphan - S01E01 - One",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            SubtitleToTranslate = $"{showDir}/S01E01.en.srt",
            MediaType = MediaType.Episode,
            Status = TranslationStatus.Completed
        });
        await _db.SaveChangesAsync();

        await _trigger.EnqueueAsync(EpisodeTarget(showDir, "S01E01", "Orphan - S01E01 - One"));
        await _trigger.EnqueueAsync(EpisodeTarget(showDir, "S01E02", "Orphan - S01E02 - Two"));

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t => t.Title == "Orphan - S01E01 - One")),
            Times.Never);
        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.Title == "Orphan - S01E02 - Two"
                && t.MediaId == null
                && t.MediaType == MediaType.Episode)),
            Times.Once);
    }

    private void SetupEpisodeSource(string directory, string stem)
    {
        _locator.Setup(l => l.FindMkv(directory, stem)).Returns($"{directory}/{stem}.mkv");
        _subtitles.Setup(s => s.GetSubtitles(directory, stem))
            .ReturnsAsync(new List<Subtitles>
            {
                new()
                {
                    Path = $"{directory}/{stem}.en.srt",
                    FileName = $"{stem}.en",
                    Language = "en",
                    Caption = "",
                    Format = ".srt"
                }
            });
    }

    private static PlexTranslationTarget EpisodeTarget(string directory, string stem, string title) =>
        new()
        {
            MediaType = MediaType.Episode,
            Directory = directory,
            FileName = stem,
            FileExtension = ".mkv",
            DisplayTitle = title,
            RatingKey = stem,
            MappedFilePath = $"{directory}/{stem}.mkv"
        };

    private static PlexTranslationTarget PathTarget() =>
        new()
        {
            MediaType = MediaType.Movie,
            Directory = Directory,
            FileName = Stem,
            FileExtension = ".mkv",
            DisplayTitle = "Unknown (2020)",
            RatingKey = "99",
            MappedFilePath = MkvPath
        };
}
