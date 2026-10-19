using System.IO;
using Lingarr.Server.Services.Media;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class MkvFileFingerprintTests
{
    [Fact]
    public void ForPath_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MkvFileFingerprint.ForPath(null));
        Assert.Equal(string.Empty, MkvFileFingerprint.ForPath(" "));
    }

    [Fact]
    public void ForPath_MissingFile_ReturnsPath()
    {
        const string path = "/movies/missing/movie.mkv";
        Assert.Equal(path, MkvFileFingerprint.ForPath(path));
    }

    [Fact]
    public void ForPath_ExistingFile_IncludesSizeAndMtime()
    {
        var path = Path.Combine(Path.GetTempPath(), "lingarr-fingerprint-" + Path.GetRandomFileName() + ".mkv");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var fingerprint = MkvFileFingerprint.ForPath(path);
            var info = new FileInfo(path);

            Assert.Equal($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", fingerprint);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
