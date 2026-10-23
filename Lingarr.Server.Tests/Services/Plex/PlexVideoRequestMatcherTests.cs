using Lingarr.Server.Services.Media;
using Lingarr.Server.Services.Plex;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexVideoRequestMatcherTests
{
    [Fact]
    public void IsSameVideo_TaggedSidecar_MatchesStem()
    {
        Assert.True(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/Foo/Foo.en.srt",
            "/movies/Foo",
            "Foo"));
    }

    [Fact]
    public void IsSameVideo_UntaggedSidecar_MatchesStem()
    {
        Assert.True(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/Foo/Foo.srt",
            "/movies/Foo",
            "Foo"));
    }

    [Fact]
    public void IsSameVideo_EmbeddedPlaceholder_MatchesMkv()
    {
        var placeholder = EmbeddedSubtitlePlaceholder.Create("/movies/Foo/Foo.mkv", 2);
        Assert.True(PlexVideoRequestMatcher.IsSameVideo(placeholder, "/movies/Foo", "Foo"));
    }

    [Fact]
    public void IsSameVideo_DifferentDirectory_DoesNotMatch()
    {
        Assert.False(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/Bar/Foo.en.srt",
            "/movies/Foo",
            "Foo"));
    }

    [Fact]
    public void IsSameVideo_DifferentStem_DoesNotMatch()
    {
        Assert.False(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/Foo/Food.en.srt",
            "/movies/Foo",
            "Foo"));
    }

    [Fact]
    public void IsSameVideo_LanguageAndCaptionSuffix_MatchesStem()
    {
        Assert.True(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/Foo/Foo.en.forced.srt",
            "/movies/Foo",
            "Foo"));
        Assert.True(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/Foo/Foo.zh-CN.srt",
            "/movies/Foo",
            "Foo"));
    }

    [Fact]
    public void IsSameVideo_LongerTitleSharingPrefix_DoesNotMatch()
    {
        Assert.False(PlexVideoRequestMatcher.IsSameVideo(
            "/movies/The.Office.en.srt",
            "/movies",
            "The"));
    }
}
