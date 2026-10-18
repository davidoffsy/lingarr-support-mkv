using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services.Media;

public class EmbeddedSubtitleService : IEmbeddedSubtitleService
{
    public const string ExtractingSuffix = ".extracting";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ExtractGates = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ImageSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle",
        "dvd_subtitle",
        "dvb_subtitle",
        "dvb_teletext",
        "xsub",
        "pgssub",
        "dvdsub",
        "pgs"
    };

    private static readonly JsonSerializerOptions ProbeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IExternalProcessRunner _processRunner;
    private readonly LanguageCodeService _languageCodeService;
    private readonly ISettingService _settingService;
    private readonly ILogger<EmbeddedSubtitleService> _logger;

    public EmbeddedSubtitleService(
        IExternalProcessRunner processRunner,
        LanguageCodeService languageCodeService,
        ISettingService settingService,
        ILogger<EmbeddedSubtitleService> logger)
    {
        _processRunner = processRunner;
        _languageCodeService = languageCodeService;
        _settingService = settingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddedSubtitleTrack>> ProbeAsync(
        string mkvPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ProbeDetailedAsync(mkvPath, cancellationToken);
        return result.Tracks;
    }

    /// <inheritdoc />
    public async Task<EmbeddedSubtitleProbeResult> ProbeDetailedAsync(
        string mkvPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsReadableMkv(mkvPath))
        {
            return EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonNoMkv);
        }

        var arguments = new[]
        {
            "-v", "error",
            "-select_streams", "s",
            "-show_entries",
            "format=duration:stream=index,codec_name:stream_tags=language,title:stream_disposition=default,forced,hearing_impaired",
            "-of", "json",
            mkvPath
        };

        ExternalProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                FfmpegExecutable.Ffprobe,
                arguments,
                ProbeTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed while listing subtitle tracks in {Path}.", mkvPath);
            return EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonProbeFailed);
        }

        if (!result.ExecutableFound)
        {
            _logger.LogWarning(
                "ffprobe was not found; embedded text subtitles in {Path} will be skipped.",
                mkvPath);
            return EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonNoFfprobe);
        }

        if (result.TimedOut)
        {
            _logger.LogWarning("ffprobe timed out while listing subtitle tracks in {Path}.", mkvPath);
            return EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonTimeout);
        }

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffprobe exited {ExitCode} while listing subtitle tracks in {Path}: {Error}",
                result.ExitCode,
                mkvPath,
                ProcessOutputTruncator.Tail(result.StandardError));
            return EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonProbeFailed);
        }

        var parsed = ParseTracks(result.StandardOutput, mkvPath);
        if (parsed == null)
        {
            return EmbeddedSubtitleProbeResult.NotFound(EmbeddedSubtitleProbeResult.ReasonProbeFailed);
        }

        return EmbeddedSubtitleProbeResult.Success(
            parsed.Value.Tracks,
            parsed.Value.Skipped,
            parsed.Value.DurationSeconds);
    }

    /// <inheritdoc />
    public async Task<string?> ExtractAsync(
        string mkvPath,
        EmbeddedSubtitleTrack track,
        CancellationToken cancellationToken = default)
    {
        var result = await ExtractDetailedAsync(
            mkvPath,
            track,
            cancellationToken,
            timeout: ExtractTimeout.ResolveAutomatic());
        return result.SidecarPath;
    }

    /// <inheritdoc />
    public async Task<EmbeddedSubtitleExtractResult> ExtractDetailedAsync(
        string mkvPath,
        EmbeddedSubtitleTrack track,
        CancellationToken cancellationToken = default,
        IProgress<FfmpegExtractProgress>? progress = null,
        double? durationSeconds = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (!IsReadableMkv(mkvPath))
        {
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.MissingMkv);
        }

        if (track.Index < 0 || !FfmpegExtractCommand.TryGetMuxer(track.Extension, out _))
        {
            _logger.LogWarning(
                "Refusing to extract subtitle stream {Index} ({Codec}) from {Path}.",
                track.Index,
                track.CodecName,
                mkvPath);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.UnsupportedTrack);
        }

        string outputPath;
        try
        {
            outputPath = BuildSidecarPath(mkvPath, track);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build sidecar path for stream {Index} of {Path}.", track.Index, mkvPath);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.FfmpegFailed);
        }

        var gate = ExtractGates.GetOrAdd(outputPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ExtractLockedAsync(
                mkvPath,
                track,
                outputPath,
                cancellationToken,
                progress,
                durationSeconds,
                timeout);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EmbeddedSubtitleExtractResult> ExtractLockedAsync(
        string mkvPath,
        EmbeddedSubtitleTrack track,
        string outputPath,
        CancellationToken cancellationToken,
        IProgress<FfmpegExtractProgress>? progress,
        double? durationSeconds,
        TimeSpan? timeout)
    {
        if (HasNonEmptyFile(outputPath))
        {
            _logger.LogDebug("Sidecar {OutputPath} already exists; skipping extract.", outputPath);
            progress?.Report(new FfmpegExtractProgress(100, true));
            return EmbeddedSubtitleExtractResult.Ok(outputPath);
        }

        if (progress != null && durationSeconds is null)
        {
            durationSeconds = await TryProbeDurationAsync(mkvPath, cancellationToken);
        }

        progress?.Report(new FfmpegExtractProgress(0, false));

        if (!FfmpegExtractCommand.TryGetMuxer(track.Extension, out var muxer))
        {
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.UnsupportedTrack);
        }

        var tempPath = outputPath + ExtractingSuffix;
        TryDeleteIncomplete(tempPath);

        var arguments = FfmpegExtractCommand.BuildArguments(mkvPath, track.Index, muxer, tempPath);

        var resolvedTimeout = timeout ?? await GetExtractTimeoutAsync();
        var lastPercent = 0;

        ExternalProcessResult result;
        try
        {
            result = await _processRunner.RunStreamingAsync(
                FfmpegExecutable.Ffmpeg,
                arguments,
                resolvedTimeout,
                line =>
                {
                    if (FfmpegProgressParser.IsEnd(line))
                    {
                        progress?.Report(new FfmpegExtractProgress(100, true));
                        return;
                    }

                    if (!FfmpegProgressParser.TryParseOutTimeUs(line, out var outTimeUs))
                    {
                        return;
                    }

                    HangfireJobHeartbeat.Touch();

                    if (durationSeconds is not > 0)
                    {
                        progress?.Report(new FfmpegExtractProgress(null, false));
                        return;
                    }

                    var percent = FfmpegProgressParser.ToPercent(outTimeUs, durationSeconds.Value);
                    if (percent is null || percent == lastPercent)
                    {
                        return;
                    }

                    lastPercent = percent.Value;
                    progress?.Report(new FfmpegExtractProgress(percent, false));
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryDeleteIncomplete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteIncomplete(tempPath);
            _logger.LogWarning(ex, "ffmpeg failed while extracting stream {Index} from {Path}.", track.Index, mkvPath);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.FfmpegFailed);
        }

        if (!result.ExecutableFound)
        {
            TryDeleteIncomplete(tempPath);
            _logger.LogWarning(
                "ffmpeg was not found; embedded text subtitles in {Path} will be skipped.",
                mkvPath);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.MissingFfmpeg);
        }

        if (result.TimedOut)
        {
            TryDeleteIncomplete(tempPath);
            _logger.LogWarning(
                "ffmpeg timed out while extracting stream {Index} from {Path}.",
                track.Index,
                mkvPath);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.TimedOut(resolvedTimeout));
        }

        if (!result.Succeeded)
        {
            TryDeleteIncomplete(tempPath);
            _logger.LogWarning(
                "ffmpeg exited {ExitCode} while extracting stream {Index} from {Path}: {Error}",
                result.ExitCode,
                track.Index,
                mkvPath,
                ProcessOutputTruncator.Tail(result.StandardError));
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.FfmpegFailed);
        }

        if (!HasNonEmptyFile(tempPath))
        {
            TryDeleteIncomplete(tempPath);
            _logger.LogWarning(
                "Extracted sidecar {OutputPath} from {Path} stream {Index} was missing or empty.",
                outputPath,
                mkvPath,
                track.Index);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.EmptyOutput);
        }

        if (HasNonEmptyFile(outputPath))
        {
            TryDeleteIncomplete(tempPath);
            progress?.Report(new FfmpegExtractProgress(100, true));
            return EmbeddedSubtitleExtractResult.Ok(outputPath);
        }

        try
        {
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteIncomplete(tempPath);
            _logger.LogWarning(ex, "Could not move extracted sidecar to {OutputPath}.", outputPath);
            return EmbeddedSubtitleExtractResult.Fail(EmbeddedSubtitleExtractResult.FfmpegFailed);
        }

        _logger.LogInformation(
            "Extracted embedded subtitle stream {Index} ({Language}{Caption}) from {Path} to {OutputPath}.",
            track.Index,
            track.Language,
            string.IsNullOrEmpty(track.Caption) ? string.Empty : "." + track.Caption,
            mkvPath,
            outputPath);

        progress?.Report(new FfmpegExtractProgress(100, true));
        return EmbeddedSubtitleExtractResult.Ok(outputPath);
    }

    private (IReadOnlyList<EmbeddedSubtitleTrack> Tracks, IReadOnlyList<SkippedEmbeddedSubtitleTrack> Skipped, double? DurationSeconds)?
        ParseTracks(string json, string mkvPath)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ([], [], null);
        }

        FfprobeOutput? probe;
        try
        {
            probe = JsonSerializer.Deserialize<FfprobeOutput>(json, ProbeJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ffprobe JSON for {Path}.", mkvPath);
            return null;
        }

        var durationSeconds = probe?.Format != null ? ParseDuration(probe.Format.Duration) : null;

        if (probe?.Streams == null || probe.Streams.Count == 0)
        {
            return ([], [], durationSeconds);
        }

        var tracks = new List<EmbeddedSubtitleTrack>();
        var skipped = new List<SkippedEmbeddedSubtitleTrack>();
        foreach (var stream in probe.Streams)
        {
            var isForced = IsFlagSet(stream.Disposition?.Forced);
            var isHearingImpaired = IsFlagSet(stream.Disposition?.HearingImpaired);
            var caption = isForced
                ? "forced"
                : isHearingImpaired
                    ? "sdh"
                    : string.Empty;
            var language = MapLanguage(GetTag(stream.Tags, "language"));

            if (!TryGetSubtitleExtension(stream.CodecName, out var extension))
            {
                _logger.LogDebug(
                    "Skipping subtitle stream {Index} in {Path} with unsupported codec {Codec}.",
                    stream.Index,
                    mkvPath,
                    stream.CodecName);
                skipped.Add(new SkippedEmbeddedSubtitleTrack
                {
                    Index = stream.Index,
                    CodecName = stream.CodecName ?? string.Empty,
                    Reason = SkipReason(stream.CodecName),
                    Language = language,
                    Caption = caption,
                    IsForced = isForced
                });
                continue;
            }

            tracks.Add(new EmbeddedSubtitleTrack
            {
                Index = stream.Index,
                CodecName = stream.CodecName ?? string.Empty,
                Language = language,
                Caption = caption,
                Title = GetTag(stream.Tags, "title") ?? string.Empty,
                Extension = extension,
                IsDefault = IsFlagSet(stream.Disposition?.Default),
                IsForced = isForced,
                IsHearingImpaired = isHearingImpaired
            });
        }

        return (tracks, skipped, durationSeconds);
    }

    private static string SkipReason(string? codecName)
    {
        if (!string.IsNullOrWhiteSpace(codecName) && ImageSubtitleCodecs.Contains(codecName.Trim()))
        {
            return EmbeddedSubtitleProbeResult.SkipReasonImage;
        }

        return EmbeddedSubtitleProbeResult.SkipReasonUnsupported;
    }

    private string MapLanguage(string? mkvLanguageTag)
    {
        if (string.IsNullOrWhiteSpace(mkvLanguageTag))
        {
            return EmbeddedSubtitleTrack.UnknownLanguage;
        }

        var tag = mkvLanguageTag.Trim();
        if (tag.Equals(EmbeddedSubtitleTrack.UndeterminedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddedSubtitleTrack.UndeterminedLanguage;
        }

        // ISO 639-2/B Chinese is not indexed by CultureInfo (which uses 639-2/T "zho").
        if (tag.Equals("chi", StringComparison.OrdinalIgnoreCase))
        {
            tag = "zho";
        }

        if (!_languageCodeService.Validate(tag))
        {
            return EmbeddedSubtitleTrack.UnknownLanguage;
        }

        try
        {
            var normalized = LanguageCodeService.GetNormalizedCode(tag);
            if (tag.Contains('-', StringComparison.Ordinal))
            {
                return normalized;
            }

            var culture = CultureInfo.GetCultureInfo(normalized);
            var twoLetter = culture.TwoLetterISOLanguageName;
            if (!string.IsNullOrEmpty(twoLetter) &&
                twoLetter.Length == 2 &&
                !twoLetter.Equals("iv", StringComparison.OrdinalIgnoreCase))
            {
                return twoLetter.ToLowerInvariant();
            }

            return normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or CultureNotFoundException)
        {
            return EmbeddedSubtitleTrack.UnknownLanguage;
        }
    }

    private static string BuildSidecarPath(string mkvPath, EmbeddedSubtitleTrack track)
    {
        var directory = Path.GetDirectoryName(mkvPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(mkvPath);
        return Path.Combine(directory, track.BuildSidecarFileName(stem));
    }

    private bool IsReadableMkv(string mkvPath)
    {
        if (string.IsNullOrWhiteSpace(mkvPath))
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(mkvPath), MediaFileLocator.MkvExtension, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping embedded subtitle work because {Path} is not an MKV.", mkvPath);
            return false;
        }

        if (!File.Exists(mkvPath))
        {
            _logger.LogDebug("MKV {Path} does not exist; skipping embedded subtitle work.", mkvPath);
            return false;
        }

        return true;
    }

    private static bool TryGetSubtitleExtension(string? codecName, out string extension)
    {
        extension = codecName?.Trim().ToLowerInvariant() switch
        {
            "subrip" or "srt" => ".srt",
            "ass" => ".ass",
            "ssa" => ".ssa",
            _ => string.Empty
        };

        return extension.Length > 0;
    }

    private static bool IsFlagSet(int? value) => value is > 0;

    private static string? GetTag(Dictionary<string, string>? tags, string key)
    {
        if (tags == null)
        {
            return null;
        }

        foreach (var pair in tags)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static bool HasNonEmptyFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteIncomplete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a failed extract.
        }
    }

    private async Task<TimeSpan> GetExtractTimeoutAsync()
    {
        var value = await _settingService.GetSetting(SettingKeys.Translation.ExtractTimeoutMinutes);
        return ExtractTimeout.Resolve(value);
    }

    private async Task<double?> TryProbeDurationAsync(string mkvPath, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "json",
            mkvPath
        };

        try
        {
            var result = await _processRunner.RunAsync(
                FfmpegExecutable.Ffprobe,
                arguments,
                ProbeTimeout,
                cancellationToken);
            if (!result.Succeeded)
            {
                return null;
            }

            var probe = JsonSerializer.Deserialize<FfprobeOutput>(result.StandardOutput, ProbeJsonOptions);
            return probe?.Format != null ? ParseDuration(probe.Format.Duration) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not probe duration for {Path}.", mkvPath);
            return null;
        }
    }

    private static double? ParseDuration(JsonElement duration)
    {
        switch (duration.ValueKind)
        {
            case JsonValueKind.Number when duration.TryGetDouble(out var numeric) && numeric > 0:
                return numeric;
            case JsonValueKind.String:
            {
                var text = duration.GetString();
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                {
                    return parsed;
                }

                break;
            }
        }

        return null;
    }

    private sealed class FfprobeOutput
    {
        [JsonPropertyName("streams")]
        public List<FfprobeStream>? Streams { get; set; }

        [JsonPropertyName("format")]
        public FfprobeFormat? Format { get; set; }
    }

    private sealed class FfprobeFormat
    {
        [JsonPropertyName("duration")]
        public JsonElement Duration { get; set; }
    }

    private sealed class FfprobeStream
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("disposition")]
        public FfprobeDisposition? Disposition { get; set; }

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }
    }

    private sealed class FfprobeDisposition
    {
        [JsonPropertyName("default")]
        public int Default { get; set; }

        [JsonPropertyName("forced")]
        public int Forced { get; set; }

        [JsonPropertyName("hearing_impaired")]
        public int HearingImpaired { get; set; }
    }
}
