using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
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

public class PlexRatingTriggerTests : IDisposable
{
    private const string MkvPath = "/movies/Foo/Foo.mkv";
    private const string SidecarPath = "/movies/Foo/Foo.en.srt";

    private readonly LingarrDbContext _db;
    private readonly Mock<ISubtitleService> _subtitles = new();
    private readonly Mock<IMediaFileLocator> _locator = new();
    private readonly Mock<IEmbeddedSubtitleService> _embedded = new();
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<ITranslationRequestService> _requests = new();
    private readonly Mock<IPlexSubtitleImporter> _importer = new();
    private readonly PlexRatingTrigger _trigger;
    private readonly Movie _movie;

    public PlexRatingTriggerTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase("plex-rating-trigger-" + Guid.NewGuid())
            .Options;
        _db = new LingarrDbContext(options);

        _movie = new Movie
        {
            RadarrId = 1,
            Title = "Foo",
            Path = "/movies/Foo",
            FileName = "Foo",
            DateAdded = DateTimeOffset.UtcNow
        };
        _db.Movies.Add(_movie);
        _db.SaveChanges();

        SetupLanguages(source: "en", target: "zh");
        _settings.Setup(s => s.GetSetting(SettingKeys.Translation.IgnoreCaptions))
            .ReturnsAsync("false");
        _settings.Setup(s => s.GetSetting(SettingKeys.Translation.ScanEmbeddedSubtitles))
            .ReturnsAsync("false");

        _locator.Setup(l => l.FindMkv(It.IsAny<IMedia>())).Returns(MkvPath);
        _subtitles.Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
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
    public async Task Enqueue_Sidecar_CreatesRequestWithRealPathAndNeverExtracts()
    {
        SetupSidecar(SidecarPath, "en");
        SetupEmbedded(EnglishTrack(2));

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.MediaId == _movie.Id
                && t.MediaType == MediaType.Movie
                && t.SubtitlePath == SidecarPath
                && t.SourceLanguage == "en"
                && t.TargetLanguage == "zh"
                && t.SubtitleFormat == ".srt")),
            Times.Once);
        VerifyNeverExtracts();
        Assert.True(string.IsNullOrEmpty(_movie.MediaHash));
    }

    [Fact]
    public async Task Enqueue_OnlyEmbedded_CreatesRequestWithPlaceholder()
    {
        SetupEmbedded(EnglishTrack(2));

        await _trigger.EnqueueAsync(ResolvedMovie());

        var expected = EmbeddedSubtitlePlaceholder.Create(MkvPath, 2);
        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == expected
                && t.SubtitlePath.StartsWith("embedded:")
                && t.SubtitlePath.Contains(MkvPath)
                && t.SubtitlePath.Contains(":2")
                && t.SubtitleFormat == ".srt"
                && t.SourceLanguage == "en"
                && t.TargetLanguage == "zh")),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_ProbesEvenWhenScanEmbeddedDisabled()
    {
        _settings.Setup(s => s.GetSetting(SettingKeys.Translation.ScanEmbeddedSubtitles))
            .ReturnsAsync("false");
        SetupSidecar(SidecarPath, "en");

        await _trigger.EnqueueAsync(ResolvedMovie());

        _locator.Verify(l => l.FindMkv(It.IsAny<IMedia>()), Times.Once);
        _embedded.Verify(s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_TargetZhSidecarExists_DoesNotCreateRequest()
    {
        SetupSidecars(
            Sidecar(SidecarPath, "en"),
            Sidecar("/movies/Foo/Foo.zh.srt", "zh"));

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_CompletedRequestForTarget_DoesNotCreateRequest()
    {
        SetupSidecar(SidecarPath, "en");
        _db.TranslationRequests.Add(new TranslationRequest
        {
            MediaId = _movie.Id,
            Title = "Foo",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            SubtitleToTranslate = SidecarPath,
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed
        });
        await _db.SaveChangesAsync();

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_ProbeThrows_StillEnqueuesFromSidecar()
    {
        SetupSidecar(SidecarPath, "en");
        _embedded.Setup(s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffprobe failed"));

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t => t.SubtitlePath == SidecarPath)),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_UniqueUndeterminedEmbedded_CreatesRequestWithUnknownSource()
    {
        SetupEmbedded(new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = EmbeddedSubtitleTrack.UndeterminedLanguage,
            Extension = ".srt"
        });

        await _trigger.EnqueueAsync(ResolvedMovie());

        var expected = EmbeddedSubtitlePlaceholder.Create(MkvPath, 2);
        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == expected
                && t.SourceLanguage == EmbeddedSubtitleTrack.UndeterminedLanguage
                && t.TargetLanguage == "zh"
                && t.SubtitleFormat == ".srt")),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_MultipleUndeterminedEmbedded_DoesNotCreateRequest()
    {
        SetupEmbedded(
            new EmbeddedSubtitleTrack
            {
                Index = 2,
                CodecName = "subrip",
                Language = EmbeddedSubtitleTrack.UndeterminedLanguage,
                Extension = ".srt"
            },
            new EmbeddedSubtitleTrack
            {
                Index = 3,
                CodecName = "subrip",
                Language = EmbeddedSubtitleTrack.UnknownLanguage,
                Extension = ".srt"
            });

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_UntaggedSidecar_CreatesRequestWithUnknownSourceLanguage()
    {
        const string untaggedPath = "/movies/Foo/Foo.srt";
        SetupSidecar(untaggedPath, "unknown");

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.MediaId == _movie.Id
                && t.MediaType == MediaType.Movie
                && t.SubtitlePath == untaggedPath
                && t.SourceLanguage == "unknown"
                && t.TargetLanguage == "zh"
                && t.SubtitleFormat == ".srt")),
            Times.Once);
        _importer.Verify(
            i => i.ListAsync("42", MediaType.Movie, It.IsAny<CancellationToken>()),
            Times.Once);
        _importer.Verify(
            i => i.MaterializeAsync(
                It.IsAny<PlexSourceSubtitleCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_UntaggedSidecar_PrefersTaggedPlexStream()
    {
        const string untaggedPath = "/movies/Foo/Foo.srt";
        const string imported = "/movies/Foo/Foo.en.srt";
        SetupSidecar(untaggedPath, "unknown");
        SetupPlexImport("/library/streams/12345", imported);

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == imported
                && t.SourceLanguage == "en"
                && t.TargetLanguage == "zh")),
            Times.Once);
        _importer.Verify(
            i => i.MaterializeAsync(
                It.IsAny<PlexSourceSubtitleCandidate>(),
                _movie.Path!,
                _movie.FileName!,
                It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_UniqueUndeterminedEmbedded_PrefersTaggedPlexStream()
    {
        const string imported = "/movies/Foo/Foo.en.srt";
        SetupEmbedded(new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = EmbeddedSubtitleTrack.UndeterminedLanguage,
            Extension = ".srt"
        });
        SetupPlexImport("/library/streams/12345", imported);

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == imported
                && t.SourceLanguage == "en")),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_PlexImportFails_FallsBackToUntaggedSidecar()
    {
        const string untaggedPath = "/movies/Foo/Foo.srt";
        SetupSidecar(untaggedPath, "unknown");
        SetupPlexImport("/library/streams/9", importedPath: null);

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == untaggedPath
                && t.SourceLanguage == "unknown")),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_SidecarPresent_DoesNotQueryPlexImporter()
    {
        SetupSidecar(SidecarPath, "en");

        await _trigger.EnqueueAsync(ResolvedMovie());

        _importer.Verify(
            i => i.ListAsync(It.IsAny<string?>(), It.IsAny<MediaType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _importer.Verify(
            i => i.MaterializeAsync(
                It.IsAny<PlexSourceSubtitleCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Enqueue_NoLocalSource_ImportsPlexOpenSubtitlesAndCreatesRequest()
    {
        const string imported = "/movies/Foo/Foo.en.srt";
        var plexCandidate = new PlexSourceSubtitleCandidate
        {
            Path = string.Empty,
            Language = "en",
            Format = ".srt",
            IsPlexStream = true,
            StreamKey = "/library/streams/12345"
        };
        _importer.Setup(i => i.ListAsync("42", MediaType.Movie, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { plexCandidate });
        _importer.Setup(i => i.MaterializeAsync(
                It.Is<PlexSourceSubtitleCandidate>(c => c.StreamKey == "/library/streams/12345"),
                _movie.Path!,
                _movie.FileName!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(imported);

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == imported
                && t.SourceLanguage == "en"
                && t.TargetLanguage == "zh"
                && t.SubtitleFormat == ".srt")),
            Times.Once);
        _importer.Verify(
            i => i.MaterializeAsync(
                It.IsAny<PlexSourceSubtitleCandidate>(),
                _movie.Path!,
                _movie.FileName!,
                It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyNeverExtracts();
    }

    [Fact]
    public async Task Enqueue_PlexImportFails_DoesNotCreateRequest()
    {
        var plexCandidate = new PlexSourceSubtitleCandidate
        {
            Path = string.Empty,
            Language = "en",
            Format = ".srt",
            IsPlexStream = true,
            StreamKey = "/library/streams/9"
        };
        _importer.Setup(i => i.ListAsync("42", MediaType.Movie, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { plexCandidate });
        _importer.Setup(i => i.MaterializeAsync(
                It.IsAny<PlexSourceSubtitleCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_NoRatingKey_DoesNotCallImporter()
    {
        await _trigger.EnqueueAsync(ResolvedMovie(ratingKey: null));

        _importer.Verify(
            i => i.ListAsync(It.IsAny<string?>(), It.IsAny<MediaType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_EmbeddedTargetTrack_CountsAsDone()
    {
        SetupEmbedded(
            EnglishTrack(2),
            new EmbeddedSubtitleTrack
            {
                Index = 3,
                CodecName = "subrip",
                Language = "zh",
                Extension = ".srt"
            });

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_EmbeddedPgsTarget_CountsAsDone()
    {
        SetupEmbedded(
            [EnglishTrack(2)],
            [
                new SkippedEmbeddedSubtitleTrack
                {
                    Index = 4,
                    CodecName = "hdmv_pgs_subtitle",
                    Reason = EmbeddedSubtitleProbeResult.SkipReasonImage,
                    Language = "zh"
                }
            ]);

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_EmbeddedForcedTargetOnly_StillCreatesRequest()
    {
        SetupEmbedded(
            EnglishTrack(2),
            new EmbeddedSubtitleTrack
            {
                Index = 3,
                CodecName = "subrip",
                Language = "zh",
                Caption = "forced",
                Extension = ".srt",
                IsForced = true
            });

        await _trigger.EnqueueAsync(ResolvedMovie());

        _requests.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t => t.TargetLanguage == "zh")),
            Times.Once);
    }

    private PlexMediaResolveResult ResolvedMovie(string? ratingKey = "42") =>
        PlexMediaResolveResult.Hit(MediaType.Movie, _movie.Id, MkvPath, ratingKey, "Foo");

    private void SetupLanguages(string source, string target)
    {
        _settings.Setup(s => s.GetSettingAsJson<SourceLanguage>(SettingKeys.Translation.SourceLanguages))
            .ReturnsAsync(new List<SourceLanguage> { new() { Code = source, Name = "English" } });
        _settings.Setup(s => s.GetSettingAsJson<TargetLanguage>(SettingKeys.Translation.TargetLanguages))
            .ReturnsAsync(new List<TargetLanguage> { new() { Code = target, Name = "Chinese" } });
    }

    private void SetupPlexImport(string streamKey, string? importedPath, string language = "en")
    {
        var plexCandidate = new PlexSourceSubtitleCandidate
        {
            Path = string.Empty,
            Language = language,
            Format = ".srt",
            IsPlexStream = true,
            StreamKey = streamKey
        };
        _importer.Setup(i => i.ListAsync("42", MediaType.Movie, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { plexCandidate });
        _importer.Setup(i => i.MaterializeAsync(
                It.Is<PlexSourceSubtitleCandidate>(c => c.StreamKey == streamKey),
                _movie.Path!,
                _movie.FileName!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(importedPath);
    }

    private void SetupSidecar(string path, string language) =>
        SetupSidecars(Sidecar(path, language));

    private void SetupSidecars(params Subtitles[] sidecars)
    {
        _subtitles.Setup(s => s.GetSubtitles(_movie.Path!, _movie.FileName!))
            .ReturnsAsync(new List<Subtitles>(sidecars));
    }

    private static Subtitles Sidecar(string path, string language, string caption = "") => new()
    {
        Path = path,
        FileName = System.IO.Path.GetFileNameWithoutExtension(path),
        Language = language,
        Caption = caption,
        Format = System.IO.Path.GetExtension(path)
    };

    private void SetupEmbedded(params EmbeddedSubtitleTrack[] tracks) =>
        SetupEmbedded(tracks, []);

    private void SetupEmbedded(
        IReadOnlyList<EmbeddedSubtitleTrack> tracks,
        IReadOnlyList<SkippedEmbeddedSubtitleTrack> skipped)
    {
        _embedded.Setup(s => s.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.Success(tracks, skipped));
    }

    private static EmbeddedSubtitleTrack EnglishTrack(int index) => new()
    {
        Index = index,
        CodecName = "subrip",
        Language = "en",
        Extension = ".srt"
    };

    private void VerifyNeverExtracts()
    {
        _embedded.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _embedded.Verify(
            s => s.ExtractDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<EmbeddedSubtitleTrack>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<Lingarr.Server.Models.FfmpegExtractProgress>?>(),
                It.IsAny<double?>(),
                It.IsAny<TimeSpan?>()),
            Times.Never);
    }
}
