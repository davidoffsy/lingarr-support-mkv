using System;
using System.IO;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class SubtitleServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly Mock<ISettingService> _settings = new();
    private readonly SubtitleService _service;

    public SubtitleServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lingarr-subtitle-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _service = new(
            NullLogger<SubtitleService>.Instance,
            new LanguageCodeService(),
            _settings.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task GetAllSubtitles_UntaggedFile_IsUnknown()
    {
        Touch("Reporting Live (2026) WEBRip-1080p.srt");

        var subtitles = await _service.GetAllSubtitles(_directory);

        var subtitle = Assert.Single(subtitles);
        Assert.Equal("unknown", subtitle.Language);
        Assert.Equal("Reporting Live (2026) WEBRip-1080p", subtitle.FileName);
    }

    [Fact]
    public async Task GetAllSubtitles_TaggedFile_ExtractsLanguage()
    {
        Touch("Reporting Live (2026) WEBRip-1080p.en.srt");

        var subtitles = await _service.GetAllSubtitles(_directory);

        var subtitle = Assert.Single(subtitles);
        Assert.Equal("en", subtitle.Language);
    }

    [Fact]
    public async Task GetAllSubtitles_BilingualAss_IsIgnored()
    {
        Touch("Movie.zh.srt");
        Touch("Movie.zh.bilingual.ass");
        Touch("Movie.bilingual.ass");

        var subtitles = await _service.GetAllSubtitles(_directory);

        var subtitle = Assert.Single(subtitles);
        Assert.Equal("Movie.zh.srt", Path.GetFileName(subtitle.Path));
        Assert.DoesNotContain(subtitles, item =>
            Path.GetFileName(item.Path).Contains("bilingual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateFilePath_UntaggedSource_AddsTargetLanguage()
    {
        var original = Path.Combine(_directory, "Reporting Live (2026) WEBRip-1080p.srt");

        var path = _service.CreateFilePath(original, "zh-CN", "");

        Assert.Equal("Reporting Live (2026) WEBRip-1080p.zh-cn.srt", Path.GetFileName(path));
    }

    [Fact]
    public void SelectSourceSubtitle_PrefersConfiguredLanguageOverUntagged()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Sidecar("Movie.srt", "unknown"),
                Sidecar("Movie.en.srt", "en")
            ],
            ["en"],
            "true");

        Assert.NotNull(selected);
        Assert.Equal("en", selected.SourceLanguage);
        Assert.Equal("Movie.en.srt", Path.GetFileName(selected.Subtitle.Path));
    }

    [Fact]
    public void SelectSourceSubtitle_FallsBackToUntaggedWhenNoConfiguredSourceExists()
    {
        var selected = _service.SelectSourceSubtitle(
            [Sidecar("Reporting Live (2026) WEBRip-1080p.srt", "unknown")],
            ["en"],
            "true");

        Assert.NotNull(selected);
        Assert.Equal("unknown", selected.SourceLanguage);
        Assert.Equal("Reporting Live (2026) WEBRip-1080p.srt", Path.GetFileName(selected.Subtitle.Path));
    }

    [Fact]
    public void SelectSourceSubtitle_ReturnsNullWhenNoSourceOrUntaggedFile()
    {
        var selected = _service.SelectSourceSubtitle(
            [Sidecar("Movie.fr.srt", "fr")],
            ["en"],
            "true");

        Assert.Null(selected);
    }

    [Fact]
    public void SelectSourceSubtitle_RespectsSourceLanguageListOrder()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Sidecar("Movie.en.srt", "en"),
                Sidecar("Movie.ja.srt", "ja")
            ],
            ["ja", "en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal("ja", selected.SourceLanguage);
        Assert.Equal("Movie.ja.srt", Path.GetFileName(selected.Subtitle.Path));
    }

    [Fact]
    public void SelectSourceSubtitle_SkipsUndeterminedCodesInSourceList()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Sidecar("Movie.srt", "unknown"),
                Sidecar("Movie.en.srt", "en")
            ],
            ["und", "unknown", "en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal("en", selected.SourceLanguage);
        Assert.Equal("Movie.en.srt", Path.GetFileName(selected.Subtitle.Path));
    }

    [Fact]
    public void SelectSourceSubtitle_PrefersNonSdhWhenDefaultSdhIsListedFirst()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Embedded(2, "en", caption: "sdh"),
                Embedded(3, "en")
            ],
            ["en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal(3, selected.Subtitle.TrackIndex);
        Assert.Equal(string.Empty, selected.Subtitle.Caption);
    }

    [Fact]
    public void SelectSourceSubtitle_PrefersNonHearingImpairedWhenDefaultFlagsMatch()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Embedded(4, "en", caption: "hi"),
                Embedded(5, "en", caption: "cc"),
                Embedded(2, "en")
            ],
            ["en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Subtitle.TrackIndex);
        Assert.Equal(string.Empty, selected.Subtitle.Caption);
    }

    [Fact]
    public void SelectSourceSubtitle_FallsBackToSdhWhenItIsTheOnlyTrack()
    {
        var selected = _service.SelectSourceSubtitle(
            [Embedded(2, "en", caption: "sdh")],
            ["en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Subtitle.TrackIndex);
        Assert.Equal("sdh", selected.Subtitle.Caption);
    }

    [Fact]
    public void SelectSourceSubtitle_PrefersCleanSidecarWhenSdhSidecarIsListedFirst()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Sidecar("Movie.en.sdh.srt", "en", caption: "sdh"),
                Sidecar("Movie.en.srt", "en")
            ],
            ["en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal("Movie.en.srt", Path.GetFileName(selected.Subtitle.Path));
        Assert.Equal(string.Empty, selected.Subtitle.Caption);
    }

    [Fact]
    public void SelectSourceSubtitle_PrefersSidecarOverEmbeddedDefaultSdh()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Embedded(2, "en", caption: "sdh"),
                Sidecar("Movie.en.srt", "en")
            ],
            ["en"],
            "false");

        Assert.NotNull(selected);
        Assert.False(selected.Subtitle.IsEmbedded);
        Assert.Equal("Movie.en.srt", Path.GetFileName(selected.Subtitle.Path));
    }

    [Fact]
    public void SelectSourceSubtitle_IgnoreCaptions_FallsBackToSdhWhenNoCleanTrackExists()
    {
        var selected = _service.SelectSourceSubtitle(
            [Embedded(2, "en", caption: "sdh")],
            ["en"],
            "true");

        Assert.NotNull(selected);
        Assert.Equal("sdh", selected.Subtitle.Caption);
    }

    [Fact]
    public void SelectSourceSubtitle_PrefersNonForcedOverForcedWhenListedFirst()
    {
        var selected = _service.SelectSourceSubtitle(
            [
                Embedded(2, "en", caption: "forced"),
                Embedded(3, "en")
            ],
            ["en"],
            "false");

        Assert.NotNull(selected);
        Assert.Equal(3, selected.Subtitle.TrackIndex);
        Assert.Equal(string.Empty, selected.Subtitle.Caption);
    }

    [Fact]
    public async Task WriteSubtitles_SettingOff_KeepsTrailingIdeographicFullStop()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.StripTrailingIdeographicFullStop))
            .ReturnsAsync("false");
        var path = Path.Combine(_directory, "Movie.zh.srt");

        await _service.WriteSubtitles(path, [Cue("我回来了。")], false);

        Assert.Contains("我回来了。", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteSubtitles_SettingOn_StripsTrailingIdeographicFullStop()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.StripTrailingIdeographicFullStop))
            .ReturnsAsync("true");
        var path = Path.Combine(_directory, "Movie.zh.srt");

        await _service.WriteSubtitles(path, [Cue("我回来了。")], false);

        var body = await File.ReadAllTextAsync(path);
        Assert.Contains("我回来了", body);
        Assert.DoesNotContain("我回来了。", body);
    }

    [Fact]
    public async Task WriteSubtitles_MissingSetting_KeepsTrailingIdeographicFullStop()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.StripTrailingIdeographicFullStop))
            .ReturnsAsync((string?)null);
        var path = Path.Combine(_directory, "Movie.zh.srt");

        await _service.WriteSubtitles(path, [Cue("我回来了。")], false);

        Assert.Contains("我回来了。", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteSubtitles_InlineSettingOff_KeepsMidCueIdeographicFullStop()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.ReplaceInlineIdeographicFullStop))
            .ReturnsAsync("false");
        var path = Path.Combine(_directory, "Movie.zh.srt");

        await _service.WriteSubtitles(path, [Cue("你好。我回来了")], false);

        Assert.Contains("你好。我回来了", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteSubtitles_InlineSettingOn_ReplacesMidCueIdeographicFullStop()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.ReplaceInlineIdeographicFullStop))
            .ReturnsAsync("true");
        var path = Path.Combine(_directory, "Movie.zh.srt");

        await _service.WriteSubtitles(path, [Cue("你好。我回来了。")], false);

        var body = await File.ReadAllTextAsync(path);
        Assert.Contains("你好 我回来了。", body);
        Assert.DoesNotContain("你好。我回来了", body);
    }

    [Fact]
    public async Task WriteSubtitles_BothPeriodSettingsOn_ReplacesInlineAndStripsTrailing()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.ReplaceInlineIdeographicFullStop))
            .ReturnsAsync("true");
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.StripTrailingIdeographicFullStop))
            .ReturnsAsync("true");
        var path = Path.Combine(_directory, "Movie.zh.srt");

        await _service.WriteSubtitles(path, [Cue("你好。我回来了。")], false);

        var body = await File.ReadAllTextAsync(path);
        Assert.Contains("你好 我回来了", body);
        Assert.DoesNotContain("。", body);
    }

    private static SubtitleItem Cue(string translated) => new()
    {
        Position = 1,
        StartTime = 1000,
        EndTime = 2000,
        Lines = [translated],
        PlaintextLines = [translated],
        TranslatedLines = [translated]
    };

    private void Touch(string fileName) =>
        File.WriteAllText(Path.Combine(_directory, fileName), "1\n00:00:01,000 --> 00:00:02,000\nHi\n");

    private static Subtitles Sidecar(string fileName, string language, string caption = "") => new()
    {
        Path = $"/movies/{fileName}",
        FileName = Path.GetFileNameWithoutExtension(fileName),
        Language = language,
        Caption = caption,
        Format = ".srt"
    };

    private static Subtitles Embedded(int index, string language, string caption = "") => new()
    {
        Path = "/movies/Movie.mkv",
        FileName = "Movie",
        Language = language,
        Caption = caption,
        Format = ".srt",
        IsEmbedded = true,
        TrackIndex = index,
        CodecName = "subrip"
    };
}
