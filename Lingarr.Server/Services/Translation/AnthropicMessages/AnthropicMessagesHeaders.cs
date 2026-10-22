using Lingarr.Core;
using Lingarr.Server.Services.Translation.OpenAiCompatible;

namespace Lingarr.Server.Services.Translation.AnthropicMessages;

/// <summary>
/// Per-request Messages headers. Never written to <see cref="HttpClient.DefaultRequestHeaders"/>,
/// so one shared client can serve official Anthropic and Custom without leaking keys.
/// </summary>
internal static class AnthropicMessagesHeaders
{
    public static void Apply(
        HttpRequestMessage request,
        string? apiKey,
        string version,
        string? sessionId = null)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", $"{LingarrVersion.Name}/{LingarrVersion.Number}");

        if (OpenCodeGoHeaders.Matches(request.RequestUri))
        {
            var value = string.IsNullOrWhiteSpace(sessionId)
                ? OpenCodeGoHeaders.NewSessionId()
                : sessionId.Trim();
            request.Headers.TryAddWithoutValidation(OpenCodeGoHeaders.SessionHeaderName, value);
        }

        var key = apiKey?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", key);
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", version.Trim());
    }
}
