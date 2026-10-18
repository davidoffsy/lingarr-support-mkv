using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Probes MKV containers for extractable text subtitle tracks and copies them to sidecar files.
/// Image-based tracks cannot be extracted, but their language still counts as already present
/// for automatic translation.
/// </summary>
public interface IEmbeddedSubtitleService
{
    /// <summary>
    /// Lists extractable text subtitle tracks in an MKV file.
    /// Returns an empty list when ffprobe is missing, times out, or the file cannot be read.
    /// </summary>
    Task<IReadOnlyList<EmbeddedSubtitleTrack>> ProbeAsync(
        string mkvPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes an MKV and reports extractable tracks, skipped streams, and why probing failed.
    /// </summary>
    Task<EmbeddedSubtitleProbeResult> ProbeDetailedAsync(
        string mkvPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a probed text track to a Plex/Emby-compatible sidecar next to the video.
    /// Does not overwrite a non-empty existing sidecar. Returns the sidecar path, or <c>null</c> on failure.
    /// </summary>
    Task<string?> ExtractAsync(
        string mkvPath,
        EmbeddedSubtitleTrack track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="ExtractAsync"/> but returns a user-visible error message on failure
    /// and can report ffmpeg extract progress (0–100 of the extract itself).
    /// When <paramref name="timeout"/> is null, uses the configured on-demand extract timeout.
    /// </summary>
    Task<EmbeddedSubtitleExtractResult> ExtractDetailedAsync(
        string mkvPath,
        EmbeddedSubtitleTrack track,
        CancellationToken cancellationToken = default,
        IProgress<FfmpegExtractProgress>? progress = null,
        double? durationSeconds = null,
        TimeSpan? timeout = null);
}
