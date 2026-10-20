namespace Lingarr.Server.Services.Translation.OpenAiCompatible;

/// <summary>
/// OpenCode requires a stable <c>x-opencode-session</c> header for routing on every
/// <c>*.opencode.ai</c> endpoint (Zen, Go, and Inference APIs alike), regardless of
/// path; other OpenAI-compatible gateways must not receive this vendor header.
/// </summary>
public static class OpenCodeGoHeaders
{
    public const string SessionHeaderName = "x-opencode-session";

    public static bool Matches(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && Matches(uri);

    public static bool Matches(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        return IsOpenCodeHost(uri.Host);
    }

    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    private static bool IsOpenCodeHost(string host) =>
        host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".opencode.ai", StringComparison.OrdinalIgnoreCase);
}
