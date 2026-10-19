namespace Lingarr.Server.Services.Media;

/// <summary>
/// Whether an automatic or bulk run should ffprobe the MKV.
/// Skip when sidecars already cover every target, or when there is no
/// sidecar source and embedded scanning is off (nothing to translate).
/// </summary>
public static class EmbeddedSubtitleProbeNeed
{
    public static bool ShouldProbe(
        bool scanEmbedded,
        bool sidecarCoversAllTargets,
        bool hasSidecarSource)
    {
        if (sidecarCoversAllTargets)
        {
            return false;
        }

        return scanEmbedded || hasSidecarSource;
    }
}
