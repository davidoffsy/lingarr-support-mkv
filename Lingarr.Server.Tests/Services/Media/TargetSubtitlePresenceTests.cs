using System.Collections.Generic;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Media;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class TargetSubtitlePresenceTests
{
    [Fact]
    public void FullLanguages_IncludesNonForcedSidecar()
    {
        var present = TargetSubtitlePresence.FullLanguages(
        [
            Sidecar("en"),
            Sidecar("zh")
        ]);

        Assert.Contains("en", present);
        Assert.Contains("zh", present);
        Assert.Equal(2, present.Count);
    }

    [Fact]
    public void FullLanguages_IgnoresForcedOnlySidecar()
    {
        var present = TargetSubtitlePresence.FullLanguages(
        [
            Sidecar("en"),
            Sidecar("zh", caption: "forced")
        ]);

        Assert.Contains("en", present);
        Assert.DoesNotContain("zh", present);
    }

    [Fact]
    public void FullLanguages_CountsSdhAsPresent()
    {
        var present = TargetSubtitlePresence.FullLanguages(
        [
            Sidecar("zh", caption: "sdh")
        ]);

        Assert.Contains("zh", present);
    }

    [Fact]
    public void FullLanguages_IncludesEmbeddedTextAndPgsTracks()
    {
        var probe = EmbeddedSubtitleProbeResult.Success(
            [Track(2, "en")],
            [Skipped(4, "zh")]);

        var present = TargetSubtitlePresence.FullLanguages([], probe);

        Assert.Contains("en", present);
        Assert.Contains("zh", present);
    }

    [Fact]
    public void FullLanguages_IgnoresForcedEmbeddedAndForcedPgs()
    {
        var probe = EmbeddedSubtitleProbeResult.Success(
            [Track(2, "en"), Track(3, "zh", caption: "forced", forced: true)],
            [Skipped(4, "zh", caption: "forced", forced: true)]);

        var present = TargetSubtitlePresence.FullLanguages([], probe);

        Assert.Contains("en", present);
        Assert.DoesNotContain("zh", present);
    }

    [Fact]
    public void FullLanguages_IgnoresUndeterminedAndUnknown()
    {
        var probe = EmbeddedSubtitleProbeResult.Success(
            [Track(2, EmbeddedSubtitleTrack.UndeterminedLanguage)],
            [Skipped(4, EmbeddedSubtitleTrack.UnknownLanguage)]);

        var present = TargetSubtitlePresence.FullLanguages(
            [Sidecar(EmbeddedSubtitleTrack.UnknownLanguage)],
            probe);

        Assert.Empty(present);
    }

    [Fact]
    public void FullLanguages_IsCaseInsensitive()
    {
        var present = TargetSubtitlePresence.FullLanguages([Sidecar("ZH")]);

        Assert.Contains("zh", present);
        Assert.True(present.Contains("ZH"));
    }

    [Fact]
    public void FullLanguages_IgnoresFailedProbe()
    {
        var present = TargetSubtitlePresence.FullLanguages(
            [Sidecar("en")],
            EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonNoFfprobe));

        Assert.Contains("en", present);
        Assert.Single(present);
    }

    [Fact]
    public void Covers_ZhCnTargetMatchesZhTrack()
    {
        var present = TargetSubtitlePresence.FullLanguages(
            [],
            EmbeddedSubtitleProbeResult.Success([], [Skipped(4, "zh")]));

        Assert.True(TargetSubtitlePresence.Covers(present, "zh-CN"));
        Assert.True(TargetSubtitlePresence.Covers(present, "zh-cn"));
        Assert.True(TargetSubtitlePresence.CoversAll(present, ["zh-CN"]));
        Assert.Empty(TargetSubtitlePresence.Missing(present, ["zh-CN"]));
    }

    [Fact]
    public void Covers_ZhTargetMatchesZhCnSidecar()
    {
        var present = TargetSubtitlePresence.FullLanguages([Sidecar("zh-CN")]);

        Assert.True(TargetSubtitlePresence.Covers(present, "zh"));
    }

    [Fact]
    public void Covers_TraditionalDoesNotMatchSimplified()
    {
        var present = TargetSubtitlePresence.FullLanguages([Sidecar("zh-CN")]);

        Assert.False(TargetSubtitlePresence.Covers(present, "zh-TW"));
        Assert.Equal(["zh-TW"], TargetSubtitlePresence.Missing(present, ["zh-TW"]));
    }

    [Fact]
    public void Covers_PtBrDoesNotMatchPtPt()
    {
        var present = TargetSubtitlePresence.FullLanguages([Sidecar("pt-BR")]);

        Assert.False(TargetSubtitlePresence.Covers(present, "pt-PT"));
        Assert.True(TargetSubtitlePresence.Covers(present, "pt"));
    }

    [Fact]
    public void CoversAll_EmptyTargets_IsFalse()
    {
        Assert.False(TargetSubtitlePresence.CoversAll(["en"], []));
    }

    private static Subtitles Sidecar(string language, string caption = "") => new()
    {
        Path = $"/movies/Foo/Foo.{language}.srt",
        FileName = $"Foo.{language}",
        Language = language,
        Caption = caption,
        Format = ".srt"
    };

    private static EmbeddedSubtitleTrack Track(
        int index,
        string language,
        string caption = "",
        bool forced = false) => new()
    {
        Index = index,
        CodecName = "subrip",
        Language = language,
        Caption = caption,
        Extension = ".srt",
        IsForced = forced
    };

    private static SkippedEmbeddedSubtitleTrack Skipped(
        int index,
        string language,
        string caption = "",
        bool forced = false) => new()
    {
        Index = index,
        CodecName = "hdmv_pgs_subtitle",
        Reason = EmbeddedSubtitleProbeResult.SkipReasonImage,
        Language = language,
        Caption = caption,
        IsForced = forced
    };
}
