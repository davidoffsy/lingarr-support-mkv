using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services.Media;

/// <summary>
/// Whether an automatic scan should be retried instead of persisting a hash.
/// Complete observations (no MKV needed, or ffprobe listed streams) can be hashed.
/// </summary>
public static class IncompleteMediaObservation
{
    public const string ReasonMkvMissing = "mkv_missing";
    public const string ReasonProbeFailed = "probe_failed";

    public static bool ShouldRetry(
        bool needsMkv,
        string? mkvPath,
        EmbeddedSubtitleProbeResult? probe,
        out string reason)
    {
        reason = string.Empty;
        if (!needsMkv)
        {
            return false;
        }

        if (string.IsNullOrEmpty(mkvPath))
        {
            reason = ReasonMkvMissing;
            return true;
        }

        if (probe is { Found: true })
        {
            return false;
        }

        reason = string.IsNullOrEmpty(probe?.Reason)
            ? ReasonProbeFailed
            : probe.Reason;
        return true;
    }
}
