using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class EmbeddedSubtitleServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _mkvPath;
    private readonly Mock<IExternalProcessRunner> _processRunner = new();
    private readonly Mock<ISettingService> _settings = new();
    private readonly EmbeddedSubtitleService _service;

    public EmbeddedSubtitleServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lingarr-mkv-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _mkvPath = Path.Combine(_directory, "Dune.2021.mkv");
        File.WriteAllBytes(_mkvPath, [0x1]);

        _settings
            .Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        _service = new EmbeddedSubtitleService(
            _processRunner.Object,
            new LanguageCodeService(),
            _settings.Object,
            NullLogger<EmbeddedSubtitleService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task ProbeAsync_KeepsTextTracksAndDropsImageAndWebVtt()
    {
        SetupProbe(Succeed("""
            {
              "streams": [
                {
                  "index": 2,
                  "codec_name": "subrip",
                  "disposition": { "default": 1, "forced": 0, "hearing_impaired": 0 },
                  "tags": { "language": "eng", "title": "English" }
                },
                {
                  "index": 3,
                  "codec_name": "ass",
                  "disposition": { "default": 0, "forced": 0, "hearing_impaired": 0 },
                  "tags": { "language": "eng" }
                },
                {
                  "index": 4,
                  "codec_name": "hdmv_pgs_subtitle",
                  "disposition": { "default": 0, "forced": 0, "hearing_impaired": 0 },
                  "tags": { "language": "eng" }
                },
                {
                  "index": 5,
                  "codec_name": "dvd_subtitle",
                  "tags": { "language": "eng" }
                },
                {
                  "index": 6,
                  "codec_name": "webvtt",
                  "tags": { "language": "eng" }
                },
                {
                  "index": 7,
                  "codec_name": "ssa",
                  "tags": { "language": "jpn" }
                }
              ]
            }
            """));

        var tracks = await _service.ProbeAsync(_mkvPath);

        Assert.Equal(3, tracks.Count);
        Assert.Collection(
            tracks,
            track =>
            {
                Assert.Equal(2, track.Index);
                Assert.Equal(".srt", track.Extension);
                Assert.Equal("en", track.Language);
                Assert.True(track.IsDefault);
                Assert.Equal("English", track.Title);
            },
            track =>
            {
                Assert.Equal(3, track.Index);
                Assert.Equal(".ass", track.Extension);
            },
            track =>
            {
                Assert.Equal(7, track.Index);
                Assert.Equal(".ssa", track.Extension);
                Assert.Equal("ja", track.Language);
            });
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("chi", "zh")]
    [InlineData("zho", "zh")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en-us")]
    [InlineData("und", "und")]
    [InlineData("xyzzy", "unknown")]
    [InlineData("", "unknown")]
    public async Task ProbeAsync_MapsLanguageTags(string mkvLanguage, string expected)
    {
        var languageJson = string.IsNullOrEmpty(mkvLanguage)
            ? "{}"
            : $$"""{ "language": "{{mkvLanguage}}" }""";

        SetupProbe(Succeed($$"""
            {
              "streams": [
                {
                  "index": 2,
                  "codec_name": "subrip",
                  "tags": {{languageJson}}
                }
              ]
            }
            """));

        var tracks = await _service.ProbeAsync(_mkvPath);

        var track = Assert.Single(tracks);
        Assert.Equal(expected, track.Language);
        Assert.Equal(expected is "unknown" or "und", !track.CanAutoTranslate);
    }

    [Fact]
    public async Task ProbeAsync_MapsForcedAndSdhCaptions()
    {
        SetupProbe(Succeed("""
            {
              "streams": [
                {
                  "index": 2,
                  "codec_name": "subrip",
                  "disposition": { "default": 0, "forced": 1, "hearing_impaired": 0 },
                  "tags": { "language": "eng" }
                },
                {
                  "index": 3,
                  "codec_name": "subrip",
                  "disposition": { "default": 0, "forced": 0, "hearing_impaired": 1 },
                  "tags": { "language": "eng" }
                }
              ]
            }
            """));

        var tracks = await _service.ProbeAsync(_mkvPath);

        Assert.Equal("forced", tracks[0].Caption);
        Assert.True(tracks[0].IsForced);
        Assert.Equal("sdh", tracks[1].Caption);
        Assert.True(tracks[1].IsHearingImpaired);
        Assert.Equal("Dune.2021.en.forced.srt", tracks[0].BuildSidecarFileName("Dune.2021"));
        Assert.Equal("Dune.2021.en.sdh.srt", tracks[1].BuildSidecarFileName("Dune.2021"));
    }

    [Fact]
    public async Task ProbeAsync_MissingFfprobe_ReturnsEmpty()
    {
        SetupProbe(ExternalProcessResult.NotFound("ffprobe"));

        var tracks = await _service.ProbeAsync(_mkvPath);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task ProbeDetailedAsync_MissingFfprobe_ReturnsNoFfprobeReason()
    {
        SetupProbe(ExternalProcessResult.NotFound("ffprobe"));

        var result = await _service.ProbeDetailedAsync(_mkvPath);

        Assert.False(result.Found);
        Assert.Equal(EmbeddedSubtitleProbeResult.ReasonNoFfprobe, result.Reason);
        Assert.Empty(result.Tracks);
    }

    [Fact]
    public async Task ProbeAsync_Timeout_ReturnsEmpty()
    {
        SetupProbe(new ExternalProcessResult
        {
            FileName = "ffprobe",
            ExecutableFound = true,
            TimedOut = true,
            ExitCode = -1
        });

        var tracks = await _service.ProbeAsync(_mkvPath);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task ProbeDetailedAsync_Timeout_ReturnsTimeoutReason()
    {
        SetupProbe(new ExternalProcessResult
        {
            FileName = "ffprobe",
            ExecutableFound = true,
            TimedOut = true,
            ExitCode = -1
        });

        var result = await _service.ProbeDetailedAsync(_mkvPath);

        Assert.False(result.Found);
        Assert.Equal(EmbeddedSubtitleProbeResult.ReasonTimeout, result.Reason);
        Assert.Empty(result.Tracks);
    }

    [Fact]
    public async Task ProbeDetailedAsync_PutsPgsInSkipped()
    {
        SetupProbe(Succeed("""
            {
              "streams": [
                {
                  "index": 2,
                  "codec_name": "subrip",
                  "tags": { "language": "eng" }
                },
                {
                  "index": 4,
                  "codec_name": "hdmv_pgs_subtitle",
                  "tags": { "language": "eng" }
                }
              ]
            }
            """));

        var result = await _service.ProbeDetailedAsync(_mkvPath);

        Assert.True(result.Found);
        Assert.Equal("en", Assert.Single(result.Tracks).Language);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(4, skipped.Index);
        Assert.Equal("hdmv_pgs_subtitle", skipped.CodecName);
        Assert.Equal(EmbeddedSubtitleProbeResult.SkipReasonImage, skipped.Reason);
        Assert.Equal("en", skipped.Language);
        Assert.False(skipped.IsForced);
    }

    [Fact]
    public async Task ProbeDetailedAsync_MapsSkippedPgsLanguageAndForced()
    {
        SetupProbe(Succeed("""
            {
              "streams": [
                {
                  "index": 4,
                  "codec_name": "hdmv_pgs_subtitle",
                  "disposition": { "default": 0, "forced": 1, "hearing_impaired": 0 },
                  "tags": { "language": "chi" }
                }
              ]
            }
            """));

        var result = await _service.ProbeDetailedAsync(_mkvPath);

        Assert.True(result.Found);
        Assert.Empty(result.Tracks);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("zh", skipped.Language);
        Assert.Equal("forced", skipped.Caption);
        Assert.True(skipped.IsForced);
        Assert.Equal(EmbeddedSubtitleProbeResult.SkipReasonImage, skipped.Reason);
    }

    [Fact]
    public async Task ProbeAsync_NonMkvOrMissingFile_DoesNotRunFfprobe()
    {
        var tracks = await _service.ProbeAsync(Path.Combine(_directory, "movie.mp4"));
        Assert.Empty(tracks);

        tracks = await _service.ProbeAsync(Path.Combine(_directory, "missing.mkv"));
        Assert.Empty(tracks);

        _processRunner.Verify(
            runner => runner.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeAsync_PassesFfprobeArgumentsWithoutAShell()
    {
        IReadOnlyList<string>? arguments = null;
        _processRunner
            .Setup(runner => runner.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, TimeSpan, CancellationToken>((_, args, _, _) => arguments = args)
            .ReturnsAsync(Succeed("""{ "streams": [] }"""));

        await _service.ProbeAsync(_mkvPath);

        Assert.NotNull(arguments);
        Assert.Equal("-v", arguments[0]);
        Assert.Equal("error", arguments[1]);
        Assert.Equal("-select_streams", arguments[2]);
        Assert.Equal("s", arguments[3]);
        Assert.Equal("-show_entries", arguments[4]);
        Assert.Equal(
            "format=duration:stream=index,codec_name:stream_tags=language,title:stream_disposition=default,forced,hearing_impaired",
            arguments[5]);
        Assert.Equal("-of", arguments[6]);
        Assert.Equal("json", arguments[7]);
        Assert.Equal(_mkvPath, arguments[8]);
    }

    [Fact]
    public async Task ExtractAsync_WritesSidecarAndValidatesNonEmptyOutput()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };

        SetupExtract(writeOutput: true, outputContent: "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

        var outputPath = await _service.ExtractAsync(_mkvPath, track);

        var expected = Path.Combine(_directory, "Dune.2021.en.srt");
        Assert.Equal(expected, outputPath);
        Assert.True(File.Exists(expected));
        Assert.True(new FileInfo(expected).Length > 0);
    }

    [Fact]
    public async Task ExtractAsync_DoesNotOverwriteExistingSidecar()
    {
        var existing = Path.Combine(_directory, "Dune.2021.en.srt");
        File.WriteAllText(existing, "already extracted");

        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };

        var outputPath = await _service.ExtractAsync(_mkvPath, track);

        Assert.Equal(existing, outputPath);
        Assert.Equal("already extracted", File.ReadAllText(existing));
        _processRunner.Verify(
            runner => runner.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _processRunner.Verify(
            runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractAsync_EmptyOutput_ReturnsNull()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };

        SetupExtract(writeOutput: true, outputContent: string.Empty);

        var outputPath = await _service.ExtractAsync(_mkvPath, track);

        Assert.Null(outputPath);
        Assert.False(File.Exists(Path.Combine(_directory, "Dune.2021.en.srt")));
    }

    [Fact]
    public async Task ExtractAsync_MissingFfmpeg_ReturnsNull()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExternalProcessResult.NotFound("ffmpeg"));

        var outputPath = await _service.ExtractAsync(_mkvPath, track);

        Assert.Null(outputPath);

        var detailed = await _service.ExtractDetailedAsync(_mkvPath, track);
        Assert.Equal(EmbeddedSubtitleExtractResult.MissingFfmpeg, detailed.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_Cancellation_DeletesIncompleteSidecar()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };
        var sidecar = Path.Combine(_directory, "Dune.2021.en.srt");

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyList<string>, TimeSpan, Action<string>, CancellationToken>(
                (_, arguments, _, _, token) =>
                {
                    File.WriteAllText(arguments[^1], "partial sidecar");
                    throw new OperationCanceledException(token);
                });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ExtractAsync(_mkvPath, track, new CancellationToken(canceled: true)));

        Assert.False(File.Exists(sidecar));
        Assert.False(File.Exists(sidecar + EmbeddedSubtitleService.ExtractingSuffix));
    }

    [Fact]
    public async Task ExtractAsync_PassesCopyArgumentsForTheStreamIndex()
    {
        IReadOnlyList<string>? arguments = null;
        var track = new EmbeddedSubtitleTrack
        {
            Index = 4,
            CodecName = "ass",
            Language = "zh",
            Caption = "forced",
            Extension = ".ass"
        };

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, TimeSpan, Action<string>, CancellationToken>((_, args, _, _, _) =>
            {
                arguments = args;
                File.WriteAllText(args[^1], "[Script Info]\n");
            })
            .ReturnsAsync(Succeed(string.Empty));

        var outputPath = await _service.ExtractAsync(_mkvPath, track);

        Assert.Equal(Path.Combine(_directory, "Dune.2021.zh.forced.ass"), outputPath);
        Assert.Equal(
            FfmpegExtractCommand.BuildArguments(
                _mkvPath,
                4,
                "ass",
                outputPath + EmbeddedSubtitleService.ExtractingSuffix),
            arguments);
        Assert.False(File.Exists(outputPath + EmbeddedSubtitleService.ExtractingSuffix));
    }

    [Fact]
    public async Task ProbeDetailedAsync_ParsesFormatDuration()
    {
        SetupProbe(Succeed("""
            {
              "streams": [
                {
                  "index": 2,
                  "codec_name": "subrip",
                  "tags": { "language": "eng" }
                }
              ],
              "format": { "duration": "3600.5" }
            }
            """));

        var result = await _service.ProbeDetailedAsync(_mkvPath);

        Assert.True(result.Found);
        Assert.Equal(3600.5, result.DurationSeconds);
    }

    [Fact]
    public async Task ExtractDetailedAsync_Timeout_ReturnsTimeoutErrorAndDeletesSidecar()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };
        var sidecar = Path.Combine(_directory, "Dune.2021.en.srt");

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<string> arguments, TimeSpan _, Action<string> _, CancellationToken _) =>
            {
                File.WriteAllText(arguments[^1], "partial");
                return new ExternalProcessResult
                {
                    FileName = "ffmpeg",
                    ExecutableFound = true,
                    TimedOut = true,
                    ExitCode = -1
                };
            });

        var result = await _service.ExtractDetailedAsync(_mkvPath, track);

        Assert.False(result.Succeeded);
        Assert.Equal(EmbeddedSubtitleExtractResult.TimedOut(TimeSpan.FromMinutes(60)), result.ErrorMessage);
        Assert.False(File.Exists(sidecar));
    }

    [Fact]
    public async Task ExtractDetailedAsync_UsesConfiguredTimeout()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };
        TimeSpan? timeout = null;
        _settings
            .Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync("90");

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, TimeSpan, Action<string>, CancellationToken>(
                (_, args, captured, _, _) =>
                {
                    timeout = captured;
                    File.WriteAllText(args[^1], "1\n");
                })
            .ReturnsAsync(Succeed(string.Empty));

        await _service.ExtractDetailedAsync(_mkvPath, track);

        Assert.Equal(TimeSpan.FromMinutes(90), timeout);
    }

    [Fact]
    public async Task ExtractAsync_UsesAutomaticTimeout()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };
        TimeSpan? timeout = null;

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, TimeSpan, Action<string>, CancellationToken>(
                (_, args, captured, _, _) =>
                {
                    timeout = captured;
                    File.WriteAllText(args[^1], "1\n");
                })
            .ReturnsAsync(Succeed(string.Empty));

        await _service.ExtractAsync(_mkvPath, track);

        Assert.Equal(ExtractTimeout.ResolveAutomatic(), timeout);
    }

    [Fact]
    public async Task ExtractDetailedAsync_ReportsProgressFromOutTime()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };
        var reports = new List<FfmpegExtractProgress>();
        var progress = new CollectingProgress(reports);

        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string _,
                IReadOnlyList<string> arguments,
                TimeSpan _,
                Action<string> onLine,
                CancellationToken _) =>
            {
                File.WriteAllText(arguments[^1], "1\n");
                onLine("out_time_us=5000000");
                onLine("progress=continue");
                onLine("out_time_us=10000000");
                onLine("progress=end");
                return Succeed(string.Empty);
            });

        var result = await _service.ExtractDetailedAsync(
            _mkvPath,
            track,
            CancellationToken.None,
            progress,
            durationSeconds: 10);

        Assert.True(result.Succeeded);
        Assert.Contains(reports, report => report.Percent == 50);
        Assert.Contains(reports, report => report.Percent == 100 && report.Ended);
    }

    [Fact]
    public async Task ExtractDetailedAsync_EmptyOutput_ReturnsErrorMessage()
    {
        var track = new EmbeddedSubtitleTrack
        {
            Index = 2,
            CodecName = "subrip",
            Language = "en",
            Extension = ".srt"
        };

        SetupExtract(writeOutput: true, outputContent: string.Empty);

        var result = await _service.ExtractDetailedAsync(_mkvPath, track);

        Assert.False(result.Succeeded);
        Assert.Equal(EmbeddedSubtitleExtractResult.EmptyOutput, result.ErrorMessage);
    }

    private sealed class CollectingProgress : IProgress<FfmpegExtractProgress>
    {
        private readonly List<FfmpegExtractProgress> _reports;

        public CollectingProgress(List<FfmpegExtractProgress> reports)
        {
            _reports = reports;
        }

        public void Report(FfmpegExtractProgress value) => _reports.Add(value);
    }

    private void SetupProbe(ExternalProcessResult result)
    {
        _processRunner
            .Setup(runner => runner.RunAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private void SetupExtract(bool writeOutput, string outputContent)
    {
        _processRunner
            .Setup(runner => runner.RunStreamingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string fileName,
                IReadOnlyList<string> arguments,
                TimeSpan _,
                Action<string> _,
                CancellationToken _) =>
            {
                if (writeOutput)
                {
                    File.WriteAllText(arguments[^1], outputContent);
                }

                return Succeed(string.Empty);
            });
    }

    private static ExternalProcessResult Succeed(string stdout) => new()
    {
        FileName = "ffprobe",
        ExecutableFound = true,
        TimedOut = false,
        ExitCode = 0,
        StandardOutput = stdout
    };
}
