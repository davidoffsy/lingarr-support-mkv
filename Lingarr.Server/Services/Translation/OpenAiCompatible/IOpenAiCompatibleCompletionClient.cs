namespace Lingarr.Server.Services.Translation.OpenAiCompatible;

/// <summary>
/// One non-streaming protocol adapter of a Custom OpenAI-compatible provider. Chat Completions and
/// Responses take the same inputs, so the translation service can pick the adapter once from the
/// configured protocol and stay protocol-agnostic for the rest of the request lifetime.
/// </summary>
public interface IOpenAiCompatibleCompletionClient
{
    /// <summary>
    /// Sends one completion request and returns the assistant message content.
    /// </summary>
    Task<string> CompleteAsync(
        string endpoint,
        string? apiKey,
        string requestTemplate,
        Dictionary<string, string> replacements,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken);

    /// <summary>
    /// Translates a batch in a single request. Uses the best response format the provider accepts
    /// (json_schema, then json_object, then no response_format) and extracts the JSON payload; the
    /// returned dictionary holds exactly the positions the model answered with, so callers can
    /// retry the missing ones.
    /// </summary>
    Task<Dictionary<int, string>> CompleteBatchAsync(
        string endpoint,
        string? apiKey,
        string requestTemplate,
        Dictionary<string, string> replacements,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken);
}
