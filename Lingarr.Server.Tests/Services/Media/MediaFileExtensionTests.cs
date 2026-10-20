using Lingarr.Server.Services.Media;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class MediaFileExtensionTests
{
    [Theory]
    [InlineData("/movies/Dune.2021.MKV", ".mkv")]
    [InlineData("/movies/Dune.2021.mp4", ".mp4")]
    [InlineData("/movies/Dune.2021.", null)]
    [InlineData("/movies/Dune", null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void NormalizeFromPath_ReturnsLowerCaseExtensionOrNull(string? path, string? expected)
    {
        Assert.Equal(expected, MediaFileExtension.NormalizeFromPath(path));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData(".mkv", true)]
    [InlineData(".MKV", true)]
    [InlineData(" .mkv ", true)]
    [InlineData(".mp4", false)]
    [InlineData(".avi", false)]
    public void AllowsMkvLookup_UnknownOrMkvOnly(string? extension, bool expected)
    {
        Assert.Equal(expected, MediaFileExtension.AllowsMkvLookup(extension));
    }
}
