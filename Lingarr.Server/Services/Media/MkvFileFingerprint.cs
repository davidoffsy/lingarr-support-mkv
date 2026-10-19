namespace Lingarr.Server.Services.Media;

/// <summary>
/// Cheap identity for an MKV so the media hash can change when the
/// container is remuxed without listing streams via ffprobe.
/// </summary>
public static class MkvFileFingerprint
{
    public static string ForPath(string? mkvPath)
    {
        if (string.IsNullOrWhiteSpace(mkvPath))
        {
            return string.Empty;
        }

        try
        {
            var info = new FileInfo(mkvPath);
            if (!info.Exists)
            {
                return mkvPath;
            }

            return $"{mkvPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return mkvPath;
        }
    }
}
