using Lingarr.Server.Services.Translation.OpenAiCompatible;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation.OpenAiCompatible;

public class OpenCodeGoHeadersTests
{
    [Theory]
    [InlineData("https://opencode.ai/zen/go/v1")]
    [InlineData("https://opencode.ai/zen/go/v1/")]
    [InlineData("https://opencode.ai/zen/go/v1/chat/completions")]
    [InlineData("https://opencode.ai/zen/go")]
    [InlineData("https://opencode.ai/zen/v1")]
    [InlineData("https://opencode.ai/zen/google/v1")]
    [InlineData("https://opencode.ai/inference/go/v1")]
    [InlineData("https://opencode.ai/inference/go/v1/chat/completions")]
    [InlineData("https://opencode.ai/inference/go/openai/v1/chat/completions")]
    [InlineData("https://opencode.ai/inference/openai/v1/chat/completions")]
    [InlineData("https://opencode.ai/inference/anthropic/v1/messages")]
    [InlineData("https://opencode.ai/v1/chat/completions")]
    [InlineData("https://api.opencode.ai/zen/go/v1/chat/completions")]
    [InlineData("https://OPENCODE.AI/ZEN/GO/v1")]
    public void Matches_OpenCodeAiUrls(string url)
    {
        Assert.True(OpenCodeGoHeaders.Matches(url));
    }

    [Theory]
    [InlineData("http://ollama:11434/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://not-opencode.ai/zen/go/v1")]
    [InlineData("https://opencode.ai.example.com/zen/go/v1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void DoesNotMatch_OtherUrls(string? url)
    {
        Assert.False(OpenCodeGoHeaders.Matches(url));
    }

    [Fact]
    public void NewSessionId_IsStableFormatAndUnique()
    {
        var first = OpenCodeGoHeaders.NewSessionId();
        var second = OpenCodeGoHeaders.NewSessionId();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
        Assert.Matches("^[0-9a-f]{32}$", first);
    }
}
