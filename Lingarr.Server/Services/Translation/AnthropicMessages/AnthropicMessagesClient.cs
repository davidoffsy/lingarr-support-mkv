using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services.Translation.OpenAiCompatible;

namespace Lingarr.Server.Services.Translation.AnthropicMessages;

/// <summary>
/// Non-streaming Anthropic Messages over a configurable endpoint. Official Anthropic calls this
/// with strict parsing disabled and keeps its historical parse rules; Custom calls it in strict
/// mode, which rejects streaming, refusals, truncation and a missing <c>max_tokens</c>, and falls
/// back from tool use to plain JSON when the gateway has no tool support. Retry policy stays with
/// the caller.
/// </summary>
public sealed class AnthropicMessagesClient
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private readonly ILogger _logger;
    private readonly string _serviceLabel;
    private readonly string? _sessionId;

    /// <param name="httpClient">
    /// Shared client. Headers are set on each request, never on
    /// <see cref="HttpClient.DefaultRequestHeaders"/>, so one instance can serve several providers.
    /// </param>
    /// <param name="requestTemplateService">Builds the JSON body from the provider template.</param>
    /// <param name="logger">Logs request failures without including the API key.</param>
    /// <param name="serviceLabel">Provider name used in errors and logs.</param>
    /// <param name="sessionId">Stable session id for OpenCode Go routing; generated per request when null.</param>
    public AnthropicMessagesClient(
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
    /// Sends one Messages request and returns the assistant text.
    /// </summary>
    /// <remarks>
    /// <paramref name="strict"/> is true for Custom: enforce non-streaming, a positive
    /// <c>max_tokens</c>, and reject refusals, truncations and empty text. Official Anthropic
    /// passes false and keeps its historical parse rules.
    /// </remarks>
    public async Task<string> CompleteAsync(
        string endpoint,
        string? apiKey,
        string version,
        string requestTemplate,
        Dictionary<string, string> replacements,
        int maxTokens,
        bool strict,
        CancellationToken cancellationToken)
    {
        RequireVersion(version);

        var bodyJson = _requestTemplateService.BuildRequestBody(requestTemplate, replacements);
        if (strict)
        {
            NonStreamingPolicy.EnsureRequestIsNotStreaming(bodyJson, _serviceLabel);
        }

        bodyJson = AiMaxTokens.ApplyChat(_requestTemplateService, bodyJson, maxTokens);
        if (strict)
        {
            EnsureMaxTokens(bodyJson);
        }

        using var response = await PostAsync(endpoint, apiKey, version, bodyJson, strict, cancellationToken);
        var responseBody = await ReadSuccessfulBodyAsync(response, "API request", strict, cancellationToken);
        return strict
            ? AnthropicMessagesPayloadParser.ExtractText(responseBody, _serviceLabel)
            : AnthropicMessagesPayloadParser.ExtractTextLenient(responseBody, _serviceLabel);
    }

    /// <summary>
    /// Translates a batch in one request. Strict mode asks for tool use first and, when that shape
    /// is unavailable, falls back to a plain JSON array in the text block. Positions the model did
    /// not return are left out so the caller can retry just those lines.
    /// </summary>
    public async Task<Dictionary<int, string>> CompleteBatchAsync(
        string endpoint,
        string? apiKey,
        string version,
        string requestTemplate,
        Dictionary<string, string> replacements,
        int maxTokens,
        bool strict,
        CancellationToken cancellationToken)
    {
        RequireVersion(version);

        var bodyJson = _requestTemplateService.BuildRequestBody(requestTemplate, replacements);
        if (strict)
        {
            // A streaming template must fail before either attempt. Falling back would only send
            // the same unsupported request again.
            NonStreamingPolicy.EnsureRequestIsNotStreaming(bodyJson, _serviceLabel);
        }

        bodyJson = AiMaxTokens.ApplyChat(_requestTemplateService, bodyJson, maxTokens);
        if (strict)
        {
            EnsureMaxTokens(bodyJson);
        }

        if (!strict)
        {
            return await CompleteBatchWithToolsAsync(
                endpoint, apiKey, version, bodyJson, strict: false, cancellationToken);
        }

        try
        {
            return await CompleteBatchWithToolsAsync(
                endpoint, apiKey, version, bodyJson, strict: true, cancellationToken);
        }
        catch (Exception ex) when (IsShapeFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "{ServiceLabel} tool use failed, falling back to a plain JSON batch request",
                _serviceLabel);
            return await CompleteBatchWithJsonAsync(endpoint, apiKey, version, bodyJson, cancellationToken);
        }
    }

    private async Task<Dictionary<int, string>> CompleteBatchWithToolsAsync(
        string endpoint,
        string? apiKey,
        string version,
        string bodyJson,
        bool strict,
        CancellationToken cancellationToken)
    {
        var withTools = _requestTemplateService.SetRequestFields(bodyJson, ToolFields());
        using var response = await PostAsync(endpoint, apiKey, version, withTools, strict, cancellationToken);
        var responseBody = await ReadSuccessfulBodyAsync(response, "batch API request", strict, cancellationToken);

        try
        {
            return AnthropicMessagesPayloadParser.ExtractToolTranslations(responseBody, _serviceLabel, strict);
        }
        catch (TranslationException ex) when (!strict && ex.Message == AnthropicMessagesPayloadParser.ToolParseFailureMessage)
        {
            _logger.LogError(ex, "Failed to parse translated results from tool use");
            throw;
        }
        catch (TranslationUnusableResponseException)
        {
            throw;
        }
        catch (Exception ex) when (strict && ex is not OperationCanceledException)
        {
            // The gateway ignored tool_use and answered with text. Use that text when it is already
            // a batch payload, so a gateway without tools is not charged a second request.
            try
            {
                return BatchJsonParser.ParseJsonArray(
                    AnthropicMessagesPayloadParser.ExtractText(responseBody, _serviceLabel),
                    _logger);
            }
            catch (TranslationUnusableResponseException)
            {
                throw;
            }
            catch (TranslationParseException)
            {
                throw new TranslationException(
                    $"{_serviceLabel} batch response had no usable tool_use block.", ex);
            }
        }
    }

    private async Task<Dictionary<int, string>> CompleteBatchWithJsonAsync(
        string endpoint,
        string? apiKey,
        string version,
        string bodyJson,
        CancellationToken cancellationToken)
    {
        using var response = await PostAsync(endpoint, apiKey, version, bodyJson, strict: true, cancellationToken);
        var responseBody = await ReadSuccessfulBodyAsync(
            response, "JSON parsing batch request", strict: true, cancellationToken);
        return BatchJsonParser.ParseJsonArray(
            AnthropicMessagesPayloadParser.ExtractText(responseBody, _serviceLabel),
            _logger);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string endpoint,
        string? apiKey,
        string version,
        string bodyJson,
        bool strict,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        if (strict)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        AnthropicMessagesHeaders.Apply(request, apiKey, version, _sessionId);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<string> ReadSuccessfulBodyAsync(
        HttpResponseMessage response,
        string requestDescription,
        bool strict,
        CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "{ServiceLabel} {RequestDescription} failed with status {StatusCode}: {ResponseBody}",
                _serviceLabel,
                requestDescription,
                response.StatusCode,
                responseBody);
            throw HttpFailure.FromResponse(
                _serviceLabel,
                response,
                $"{_serviceLabel} {requestDescription} failed with status {response.StatusCode}: {responseBody}");
        }

        if (strict)
        {
            NonStreamingPolicy.EnsureResponseIsNotStreaming(
                response.Content.Headers.ContentType?.MediaType, responseBody, _serviceLabel);
        }

        return responseBody;
    }

    private void RequireVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new TranslationConfigurationException(
                $"{_serviceLabel} Anthropic Messages requires an anthropic-version header.");
        }
    }

    private void EnsureMaxTokens(string bodyJson)
    {
        using var document = JsonDocument.Parse(bodyJson);
        if (!document.RootElement.TryGetProperty("max_tokens", out var maxTokens)
            || maxTokens.ValueKind != JsonValueKind.Number
            || !maxTokens.TryGetInt32(out var value)
            || value <= 0)
        {
            throw new TranslationConfigurationException(
                $"{_serviceLabel} Anthropic Messages requires a positive max_tokens value. Set ai_max_tokens or keep max_tokens in the request template.");
        }
    }

    private static bool IsShapeFailure(Exception exception) =>
        exception is not (OperationCanceledException
            or TranslationAuthenticationException
            or TranslationConfigurationException
            or TranslationResponseException
            or TranslationUnusableResponseException);

    /// <summary>
    /// Tool definition copied from the official Anthropic batch request so both callers ask for
    /// the same position map.
    /// </summary>
    private static Dictionary<string, object?> ToolFields() => new()
    {
        ["tools"] = new[]
        {
            new
            {
                name = "record_translation_batch",
                description = "Record batch translation results using well-structured JSON.",
                input_schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["translations"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["position"] = new
                                    {
                                        type = "integer",
                                        description = "Position/index of the subtitle item"
                                    },
                                    ["line"] = new
                                    {
                                        type = "string",
                                        description = "Translated subtitle text"
                                    }
                                },
                                required = new[] { "position", "line" }
                            },
                            description = "Array of translated subtitle items with their positions"
                        }
                    },
                    required = new[] { "translations" }
                }
            }
        },
        ["tool_choice"] = new
        {
            type = "tool",
            name = "record_translation_batch"
        }
    };
}
