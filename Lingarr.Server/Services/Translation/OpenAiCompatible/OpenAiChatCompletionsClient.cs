using System.Net;
using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Integrations.Translation;

namespace Lingarr.Server.Services.Translation.OpenAiCompatible;

/// <summary>
/// Non-streaming OpenAI Chat Completions over HTTP for one provider identity.
/// Handles request-body construction, thinking/max-token injection, transient error
/// classification and response parsing. Retry and fallback policy stay with the caller
/// (the translation service), which owns the configured retry settings.
/// </summary>
public sealed class OpenAiChatCompletionsClient : IOpenAiCompatibleCompletionClient
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private readonly ILogger _logger;
    private readonly string _serviceLabel;
    private readonly string? _sessionId;
    private readonly BatchResponseFormatPolicy _responseFormatPolicy = new();

    /// <param name="httpClient">
    /// Shared client. The caller owns the per-request timeout through the cancellation token; this
    /// class never mutates the client, so one instance can serve several providers.
    /// </param>
    /// <param name="requestTemplateService">Builds the JSON body from the provider template.</param>
    /// <param name="logger">Logs request failures without leaking API keys.</param>
    /// <param name="serviceLabel">Human-readable provider name used in errors and logs.</param>
    /// <param name="sessionId">Stable session id for OpenCode Go routing; generated per request when null.</param>
    public OpenAiChatCompletionsClient(
        HttpClient httpClient,
        IRequestTemplateService requestTemplateService,
        ILogger logger,
        string serviceLabel,
        string? sessionId = null)
    {
        _httpClient = httpClient;
        _requestTemplateService = requestTemplateService;
        _logger = logger;
        _serviceLabel = serviceLabel;
        _sessionId = sessionId;
    }

    /// <summary>
    /// Sends one chat completion and returns the assistant message content.
    /// Empty choices and transport errors surface as <see cref="TranslationResponseException"/>;
    /// non-transient HTTP failures surface as <see cref="TranslationException"/>.
    /// </summary>
    public async Task<string> CompleteAsync(
        string endpoint,
        string? apiKey,
        string requestTemplate,
        Dictionary<string, string> replacements,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var bodyJson = _requestTemplateService.BuildRequestBody(requestTemplate, replacements);
        NonStreamingPolicy.EnsureRequestIsNotStreaming(bodyJson, _serviceLabel);
        using var response = await PostChatRequestAsync(endpoint, apiKey, bodyJson, thinking, maxTokens, cancellationToken);
        return await ReadChatContentAsync(endpoint, response, cancellationToken);
    }

    /// <summary>
    /// Translates a batch in a single request. Uses the best response format the provider has
    /// accepted so far: <c>json_schema</c>, then <c>json_object</c>, then no <c>response_format</c>.
    /// Repeated format rejections downgrade the mode for the rest of this client's lifetime.
    /// Unparsable responses raise <see cref="TranslationParseException"/>.
    /// </summary>
    public async Task<Dictionary<int, string>> CompleteBatchAsync(
        string endpoint,
        string? apiKey,
        string requestTemplate,
        Dictionary<string, string> replacements,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        // Reject a streaming template before either attempt. A configuration error must not fall
        // through into the JSON fallback, which would send the same streaming request again.
        var bodyJson = _requestTemplateService.BuildRequestBody(requestTemplate, replacements);
        NonStreamingPolicy.EnsureRequestIsNotStreaming(bodyJson, _serviceLabel);

        var mode = _responseFormatPolicy.Mode;
        if (mode == BatchResponseFormatMode.None)
        {
            return await CompleteBatchWithoutResponseFormatAsync(
                endpoint, apiKey, requestTemplate, replacements, thinking, maxTokens, cancellationToken);
        }

        try
        {
            var result = await CompleteBatchWithResponseFormatAsync(
                mode, endpoint, apiKey, requestTemplate, replacements, thinking, maxTokens, cancellationToken);
            _responseFormatPolicy.RecordSuccess();
            return result;
        }
        // A gateway can reject response_format while accepting the same body without it. Only that
        // specific failure is allowed to downgrade the format for later batches; the plain request
        // must succeed first, because otherwise the format was not the only problem.
        catch (TranslationResponseFormatException)
        {
            LogFormatRejection(mode);

            var result = await CompleteBatchWithoutResponseFormatAsync(
                endpoint, apiKey, requestTemplate, replacements, thinking, maxTokens, cancellationToken);

            var downgradedTo = _responseFormatPolicy.RecordFormatRejection(mode);
            if (downgradedTo != null)
            {
                _logger.LogWarning(
                    "{ServiceLabel} downgraded its batch response format to {ResponseFormat} after repeated rejections; " +
                    "later batches in this translation will use it.",
                    _serviceLabel,
                    Describe(downgradedTo.Value));
            }

            return result;
        }
        // Fall back to plain JSON only for failures that a different request shape can fix. Cancellation
        // must stay cancellation, credentials must stay credentials (the chain skips the provider), a
        // rate limit must not be re-sent immediately as a second request, and a terminal response
        // (refusal, failed payload, event stream) must not be retried as a different shape.
        catch (Exception ex) when (CanFallBackToJsonParsing(ex))
        {
            _logger.LogWarning(ex, "Structured output failed, falling back to JSON parsing");
            return await CompleteBatchWithoutResponseFormatAsync(
                endpoint, apiKey, requestTemplate, replacements, thinking, maxTokens, cancellationToken);
        }
    }

    /// <summary>
    /// A rejected response format is a recoverable capability probe, not a failed translation.
    /// The first rejection of a streak is visible as a warning; repeats stay at debug so a gateway
    /// that keeps rejecting the format does not flood the log before the policy downgrades.
    /// </summary>
    private void LogFormatRejection(BatchResponseFormatMode mode)
    {
        var attempt = _responseFormatPolicy.ConsecutiveRejections + 1;
        if (mode == BatchResponseFormatMode.JsonSchema)
        {
            if (attempt == 1)
            {
                _logger.LogWarning(
                    "{ServiceLabel} rejected the json_schema response format; retrying this batch as plain JSON. " +
                    "Later batches keep trying json_schema and switch to json_object after {Limit} consecutive rejections.",
                    _serviceLabel,
                    BatchResponseFormatPolicy.JsonSchemaRejectionLimit);
                return;
            }

            _logger.LogDebug(
                "{ServiceLabel} rejected the json_schema response format again ({Attempt}/{Limit}); " +
                "retrying this batch as plain JSON.",
                _serviceLabel,
                attempt,
                BatchResponseFormatPolicy.JsonSchemaRejectionLimit);
            return;
        }

        _logger.LogDebug(
            "{ServiceLabel} rejected the {ResponseFormat} response format; retrying this batch as plain JSON.",
            _serviceLabel,
            Describe(mode));
    }

    private static bool CanFallBackToJsonParsing(Exception exception) =>
        exception is not (OperationCanceledException
            or TranslationAuthenticationException
            or TranslationConfigurationException
            or TranslationResponseException
            or TranslationUnusableResponseException);

    private async Task<HttpResponseMessage> PostChatRequestAsync(
        string endpoint,
        string? apiKey,
        string bodyJson,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var withTokens = AiMaxTokens.ApplyChat(_requestTemplateService, bodyJson, maxTokens);
        var requestJson = AiThinking.Apply(_requestTemplateService, withTokens, thinking);
        using var request = OpenAiCompatibleHttp.CreateJsonPost(endpoint, requestJson, apiKey, _sessionId);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<string> ReadChatContentAsync(
        string endpoint,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var responseBody = await ReadSuccessfulBodyAsync(
            endpoint, response, "chat API request", cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationResponseException("Invalid or empty response from chat API.");
        }

        return chatResponse.Choices[0].Message.Content;
    }

    /// <summary>
    /// Reads the body of a response and either returns it or throws the classified HTTP failure.
    /// Event-stream answers are rejected here: a streamed fragment would otherwise be parsed as a
    /// half-finished JSON document.
    /// </summary>
    private async Task<string> ReadSuccessfulBodyAsync(
        string endpoint,
        HttpResponseMessage response,
        string requestDescription,
        CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "{ServiceLabel} {RequestDescription} to {Endpoint} failed with status {StatusCode}: {ResponseContent}",
                _serviceLabel,
                requestDescription,
                endpoint,
                response.StatusCode,
                responseBody);
            var error =
                $"{_serviceLabel} {requestDescription} failed with status {response.StatusCode}: {responseBody}";
            throw HttpFailure.FromResponse(_serviceLabel, response, error);
        }

        NonStreamingPolicy.EnsureResponseIsNotStreaming(
            response.Content.Headers.ContentType?.MediaType, responseBody, _serviceLabel);
        return responseBody;
    }

    private async Task<Dictionary<int, string>> CompleteBatchWithResponseFormatAsync(
        BatchResponseFormatMode mode,
        string endpoint,
        string? apiKey,
        string requestTemplate,
        Dictionary<string, string> replacements,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var bodyJson = BuildBatchBody(requestTemplate, replacements, mode);

        using var response = await PostChatRequestAsync(endpoint, apiKey, bodyJson, thinking, maxTokens, cancellationToken);

        // A 400/422 can describe any invalid request, not just unsupported response formats.
        // Only an explicit format rejection can change the capability policy; all other errors
        // retain their usual HTTP classification (and may still use the existing plain fallback).
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (ResponseFormatRejection.IsUnsupported(errorBody))
            {
                _logger.LogDebug(
                    "{ServiceLabel} {ResponseFormat} batch request to {Endpoint} failed with status {StatusCode}: {ResponseContent}",
                    _serviceLabel,
                    Describe(mode),
                    endpoint,
                    response.StatusCode,
                    errorBody);
                throw new TranslationResponseFormatException(
                    $"{_serviceLabel} {Describe(mode)} batch request failed with status {response.StatusCode}: {errorBody}");
            }
        }

        var responseBody = await ReadSuccessfulBodyAsync(
            endpoint, response, $"{Describe(mode)} batch request", cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);
        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationException($"No completion choices returned from {_serviceLabel}");
        }

        var translatedJson = chatResponse.Choices[0].Message.Content;
        return BatchJsonParser.ParseStructuredWrapper(translatedJson, _logger);
    }

    private async Task<Dictionary<int, string>> CompleteBatchWithoutResponseFormatAsync(
        string endpoint,
        string? apiKey,
        string requestTemplate,
        Dictionary<string, string> replacements,
        AiThinkingMode thinking,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var bodyJson = BuildBatchBody(requestTemplate, replacements, BatchResponseFormatMode.None);

        using var response = await PostChatRequestAsync(endpoint, apiKey, bodyJson, thinking, maxTokens, cancellationToken);
        var responseBody = await ReadSuccessfulBodyAsync(
            endpoint, response, "JSON parsing batch request", cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationException($"No completion choices returned from {_serviceLabel}");
        }

        var translatedJson = chatResponse.Choices[0].Message.Content;
        return BatchJsonParser.ParseJsonArray(translatedJson, _logger);
    }

    private string BuildBatchBody(
        string requestTemplate,
        Dictionary<string, string> replacements,
        BatchResponseFormatMode mode)
    {
        var effectiveReplacements = replacements;
        if (mode != BatchResponseFormatMode.JsonSchema)
        {
            // json_object requires a top-level object, matching the schema mode's wrapper. Only
            // the fallback without response_format requests a bare array.
            effectiveReplacements = new Dictionary<string, string>(replacements);
            effectiveReplacements["systemPrompt"] =
                (effectiveReplacements.GetValueOrDefault("systemPrompt") ?? string.Empty)
                + (mode == BatchResponseFormatMode.JsonObject
                    ? BatchJsonObjectInstruction
                    : BatchJsonArrayInstruction);
        }

        var bodyJson = _requestTemplateService.BuildRequestBody(requestTemplate, effectiveReplacements);
        if (mode == BatchResponseFormatMode.JsonSchema)
        {
            // Keep the historical merge behaviour so a template's schema customizations survive.
            return _requestTemplateService.SetRequestFields(bodyJson, new Dictionary<string, object?>
            {
                ["response_format"] = CreateJsonSchemaResponseFormat()
            });
        }

        // json_object must not inherit stale json_schema fields from a customized template.
        bodyJson = _requestTemplateService.RemoveRequestField(bodyJson, "response_format");
        if (mode == BatchResponseFormatMode.None)
        {
            return bodyJson;
        }

        return _requestTemplateService.SetRequestFields(bodyJson, new Dictionary<string, object?>
        {
            ["response_format"] = new { type = "json_object" }
        });
    }

    private static object CreateJsonSchemaResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "batch_translation_response",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    translations = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                position = new
                                {
                                    type = "integer",
                                    description = "Position number of the subtitle item"
                                },
                                line = new
                                {
                                    type = "string",
                                    description = "Translated subtitle text"
                                }
                            },
                            required = new[] { "position", "line" },
                            additionalProperties = false
                        }
                    }
                },
                required = new[] { "translations" },
                additionalProperties = false
            }
        }
    };

    private static string Describe(BatchResponseFormatMode mode) => mode switch
    {
        BatchResponseFormatMode.JsonSchema => "json_schema",
        BatchResponseFormatMode.JsonObject => "json_object",
        _ => "plain JSON"
    };

    private const string BatchJsonObjectInstruction =
        "\n\nReturn only a JSON object with a \"translations\" array of objects with \"position\" and \"line\" fields. " +
        "Do not add commentary. Example: {\"translations\":[{\"position\": 1, \"line\": \"translated text\"}]}";

    private const string BatchJsonArrayInstruction =
        "\n\nReturn only a JSON array of objects with \"position\" and \"line\" fields. " +
        "Do not add commentary. Example: [{\"position\": 1, \"line\": \"translated text\"}]";
}
