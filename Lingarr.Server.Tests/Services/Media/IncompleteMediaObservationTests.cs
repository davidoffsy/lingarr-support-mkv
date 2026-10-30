using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Media;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class IncompleteMediaObservationTests
{
    [Fact]
    public void NoMkvNeeded_IsComplete()
    {
        Assert.False(IncompleteMediaObservation.ShouldRetry(false, null, null, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void NeedsMkv_MissingPath_IsRetryable()
    {
        Assert.True(IncompleteMediaObservation.ShouldRetry(true, null, null, out var reason));
        Assert.Equal(IncompleteMediaObservation.ReasonMkvMissing, reason);
    }

    [Fact]
    public void NeedsMkv_ProbeSucceeded_IsComplete()
    {
        var probe = EmbeddedSubtitleProbeResult.Success([], []);
        Assert.False(IncompleteMediaObservation.ShouldRetry(true, "/tv/show.mkv", probe, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData(EmbeddedSubtitleProbeResult.ReasonTimeout)]
    [InlineData(EmbeddedSubtitleProbeResult.ReasonNoFfprobe)]
    [InlineData(EmbeddedSubtitleProbeResult.ReasonProbeFailed)]
    [InlineData(EmbeddedSubtitleProbeResult.ReasonNoMkv)]
    public void NeedsMkv_ProbeNotFound_IsRetryable(string probeReason)
    {
        var probe = EmbeddedSubtitleProbeResult.NotFound(probeReason);
        Assert.True(IncompleteMediaObservation.ShouldRetry(true, "/tv/show.mkv", probe, out var reason));
        Assert.Equal(probeReason, reason);
    }

    [Fact]
    public void NeedsMkv_ProbeNull_IsRetryable()
    {
        Assert.True(IncompleteMediaObservation.ShouldRetry(true, "/tv/show.mkv", null, out var reason));
        Assert.Equal(IncompleteMediaObservation.ReasonProbeFailed, reason);
    }
}
