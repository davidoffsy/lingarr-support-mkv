using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MediaSubtitleProcessor;

public class EmbeddedSubtitleTests : MediaSubtitleProcessorTestBase
{
    private const string MkvPath = "/movies/test/test.movie.mkv";

    [Fact]
    public async Task ProcessMedia_ScanDisabled_NoSidecars_DoesNotProbe()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        SetupProbe(movie, Track(2, "en"));

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        Assert.False(string.IsNullOrEmpty(movie.MediaHash));
        MediaFileLocatorMock.Verify(l => l.FindMkv(movie), Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_ScanEnabled_MkvMissing_DoesNotPersistHash()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        MediaFileLocatorMock.Setup(l => l.FindMkv(movie)).Returns((string?)null);

        var result = await Processor.ProcessMediaDetailed(movie, MediaType.Movie);

        Assert.False(result.Translated);
        Assert.True(result.Retryable);
        Assert.True(string.IsNullOrEmpty(movie.MediaHash));
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_ScanEnabled_ProbeTimeout_DoesNotPersistHash()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        MediaFileLocatorMock.Setup(l => l.FindMkv(movie)).Returns(MkvPath);
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonTimeout));

        var result = await Processor.ProcessMediaDetailed(movie, MediaType.Movie);

        Assert.False(result.Translated);
        Assert.True(result.Retryable);
        Assert.True(string.IsNullOrEmpty(movie.MediaHash));
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_SidecarAlreadyHasTarget_DoesNotProbe()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("en"), Sidecar("ro")]);
        SetupStandardSettings();
        SetupProbe(movie, Track(2, "en"), Track(3, "ro"));

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ProbeDetailedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_EmbeddedSource_ExtractsThenCreatesRequest()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, "en"));
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SourceLanguage == "en" &&
                t.TargetLanguage == "ro" &&
                t.SubtitlePath == "/movies/test/test.movie.en.srt" &&
                t.SubtitleFormat == ".srt")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_SidecarAndEmbeddedSameLanguage_DoesNotExtractOrDuplicate()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("en")]);
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, "en"));

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == "/movies/test/test.movie.en.srt")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_UndeterminedEmbeddedLanguage_DoesNotTranslate()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, EmbeddedSubtitleTrack.UndeterminedLanguage));

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_TargetSidecarAlreadyExists_SkipsEmbeddedSource()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("ro")]);
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, "en"));

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_EmbeddedTextTarget_Skips()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, "en"), Track(3, "ro"));
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_EmbeddedPgsTarget_Skips()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(
            movie,
            [Track(2, "en")],
            [Pgs("ro")]);
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_ScanDisabled_EmbeddedPgsTarget_Skips()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("en")]);
        SetupStandardSettings();
        SetupProbe(movie, [], [Pgs("ro")]);

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()),
            Times.Once);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_PgsSkip_SecondRun_DoesNotProbe()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("en")]);
        SetupStandardSettings();
        SetupProbe(movie, [], [Pgs("ro")]);

        Assert.False(await Processor.ProcessMedia(movie, MediaType.Movie));
        Assert.False(await Processor.ProcessMedia(movie, MediaType.Movie));

        EmbeddedSubtitleServiceMock.Verify(
            s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()),
            Times.Once);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_ZhCnTarget_PgsZh_Skips()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("en")]);
        SetupStandardSettings();
        SettingServiceMock
            .Setup(s => s.GetSettingAsJson<Lingarr.Server.Models.TargetLanguage>(
                Lingarr.Core.Configuration.SettingKeys.Translation.TargetLanguages))
            .ReturnsAsync(
            [
                new Lingarr.Server.Models.TargetLanguage { Code = "zh-CN", Name = "Chinese (Simplified)" }
            ]);
        SetupProbe(movie, [], [Pgs("zh")]);

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_EmbeddedForcedTargetOnly_StillTranslates()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(
            movie,
            [Track(2, "en"), Track(3, "ro", caption: "forced", forced: true)],
            [Pgs("ro", caption: "forced", forced: true)]);
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SourceLanguage == "en" &&
                t.TargetLanguage == "ro")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_TargetForcedSidecarOnly_StillTranslates()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(
            [
                Sidecar("en"),
                Sidecar("ro", caption: "forced")
            ]);
        SetupStandardSettings();

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SourceLanguage == "en" &&
                t.TargetLanguage == "ro" &&
                t.SubtitlePath == "/movies/test/test.movie.en.srt")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_IgnoreCaptions_SkipsForcedEmbeddedWhenRegularExists()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings(ignoreCaptions: "true");
        EnableEmbeddedScan();
        SetupProbe(
            movie,
            Track(2, "en", caption: "forced", forced: true),
            Track(3, "en", isDefault: true));
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.Is<EmbeddedSubtitleTrack>(t => t.Index == 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(MkvPath, It.Is<EmbeddedSubtitleTrack>(t => t.Index == 3), It.IsAny<CancellationToken>()),
            Times.Once);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(MkvPath, It.Is<EmbeddedSubtitleTrack>(t => t.Index == 2), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_FfprobeMissing_StillProcessesSidecar()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([Sidecar("en")]);
        SetupStandardSettings();
        EnableEmbeddedScan();
        MediaFileLocatorMock.Setup(l => l.FindMkv(movie)).Returns(MkvPath);
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonNoFfprobe));

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SubtitlePath == "/movies/test/test.movie.en.srt")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_ExtractFailure_DoesNotCreateRequestOrPersistHash()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, "en"));
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        Assert.True(string.IsNullOrEmpty(movie.MediaHash));
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_SameEmbeddedTracks_SecondRunShortCircuitsViaHash()
    {
        var movie = await CreateTestMovie();
        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => Task.FromResult(new List<Subtitles>()));
        SetupStandardSettings();
        EnableEmbeddedScan();
        SetupProbe(movie, Track(2, "en"));
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        Assert.True(await Processor.ProcessMedia(movie, MediaType.Movie));
        Assert.False(await Processor.ProcessMedia(movie, MediaType.Movie));

        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Once);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ExtractAsync(MkvPath, It.IsAny<EmbeddedSubtitleTrack>(), It.IsAny<CancellationToken>()),
            Times.Once);
        EmbeddedSubtitleServiceMock.Verify(
            s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void EnableEmbeddedScan()
    {
        SettingServiceMock
            .Setup(s => s.GetSetting(SettingKeys.Translation.ScanEmbeddedSubtitles))
            .ReturnsAsync("true");
    }

    private void SetupProbe(Lingarr.Core.Entities.Movie movie, params EmbeddedSubtitleTrack[] tracks) =>
        SetupProbe(movie, tracks, []);

    private void SetupProbe(
        Lingarr.Core.Entities.Movie movie,
        IReadOnlyList<EmbeddedSubtitleTrack> tracks,
        IReadOnlyList<SkippedEmbeddedSubtitleTrack> skipped)
    {
        MediaFileLocatorMock.Setup(l => l.FindMkv(movie)).Returns(MkvPath);
        EmbeddedSubtitleServiceMock
            .Setup(s => s.ProbeDetailedAsync(MkvPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmbeddedSubtitleProbeResult.Success(tracks, skipped));
    }

    private static Subtitles Sidecar(string language, string caption = "") => new()
    {
        Path = $"/movies/test/test.movie.{language}{(string.IsNullOrEmpty(caption) ? "" : "." + caption)}.srt",
        FileName = $"test.movie.{language}",
        Language = language,
        Caption = caption,
        Format = ".srt"
    };

    private static EmbeddedSubtitleTrack Track(
        int index,
        string language,
        string caption = "",
        bool forced = false,
        bool isDefault = false) => new()
    {
        Index = index,
        CodecName = "subrip",
        Language = language,
        Caption = caption,
        Extension = ".srt",
        IsForced = forced,
        IsDefault = isDefault
    };

    private static SkippedEmbeddedSubtitleTrack Pgs(
        string language,
        string caption = "",
        bool forced = false) => new()
    {
        Index = 4,
        CodecName = "hdmv_pgs_subtitle",
        Reason = EmbeddedSubtitleProbeResult.SkipReasonImage,
        Language = language,
        Caption = caption,
        IsForced = forced
    };
}
