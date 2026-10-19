using Lingarr.Server.Services.Media;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class EmbeddedSubtitleProbeNeedTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void ShouldProbe_MatchesExpected(
        bool scanEmbedded,
        bool sidecarCoversAllTargets,
        bool hasSidecarSource,
        bool expected)
    {
        Assert.Equal(
            expected,
            EmbeddedSubtitleProbeNeed.ShouldProbe(scanEmbedded, sidecarCoversAllTargets, hasSidecarSource));
    }
}
