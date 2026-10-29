using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Integrations.Translation;
using Lingarr.Server.Services.Translation.Base;
using Lingarr.Server.Services.Translation.LocalAi;
using Lingarr.Server.Services.Translation.OpenAiCompatible;

namespace Lingarr.Server.Services.Translation;

public class LocalAiService : BaseLanguageService, ITranslationService, IBatchTranslationService, IProofreadService, IModelOverridable
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private readonly OpenAiChatCompletionsClient _chatClient;
    private string? _model;
    private string? _endpoint;
    private string? _apiKey;
    private string? _chatRequestTemplate;
    private string? _generateRequestTemplate;
    private bool _isChatEndpoint;
    private AiThinkingMode _aiThinking;
    private int _maxTokens;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly string _sessionId = OpenCodeGoHeaders.NewSessionId();

    /// <inheritdoc />
    public override string? ModelName => _model;

    /// <inheritdoc />
    public void OverrideModel(string model) => SetModelOverride(model);

    // retry settings
    private int _maxRetries;
    private TimeSpan _retryDelay;
    private int _retryDelayMultiplier;
    private TimeSpan _requestTimeout;

    public LocalAiService(
        ISettingService settings,
        HttpClient httpClient,
        ILogger<LocalAiService> logger,
        LanguageCodeService languageCodeService,
        IRequestTemplateService requestTemplateService)
        : base(settings, logger, languageCodeService)
    {
        _httpClient = httpClient;
        TranslationRequestTimeout.DisableClientTimeout(_httpClient);
        _requestTemplateService = requestTemplateService;
        _chatClient = new OpenAiChatCompletionsClient(
            httpClient,
            requestTemplateService,
            logger,
            "LocalAI",
            _sessionId);
    }

    /// <summary>
    /// Initializes the translation service with necessary configurations and credentials.
    /// This method is thread-safe and ensures one-time initialization of service dependencies.
    /// </summary>
    /// <param name="sourceLanguage">The source language code for translation</param>
    /// <param name="targetLanguage">The target language code for translation</param>
    /// <returns>A task that represents the asynchronous initialization operation</returns>
    /// <exception cref="TranslationConfigurationException">Thrown when the endpoint is missing or unusable</exception>
    /// <exception cref="TranslationException">Thrown when this candidate has no model</exception>
    private async Task InitializeAsync(string sourceLanguage, string targetLanguage)
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var settings = await _settings.GetSettings([
                SettingKeys.Translation.LocalAi.Model,
                SettingKeys.Translation.LocalAi.Endpoint,
                SettingKeys.Translation.LocalAi.Protocol,
                SettingKeys.Translation.LocalAi.ChatRequestTemplate,
                SettingKeys.Translation.LocalAi.GenerateRequestTemplate,
                SettingKeys.Translation.AiPrompt,
                SettingKeys.Translation.AiUserPrompt,
                SettingKeys.Translation.AiMaxTokens,
                SettingKeys.Translation.AiThinking,
                SettingKeys.Translation.ProofreadPrompt,
                SettingKeys.Translation.ProofreadUserPrompt,
                SettingKeys.Translation.RequestTimeout,
                SettingKeys.Translation.MaxRetries,
                SettingKeys.Translation.RetryDelay,
                SettingKeys.Translation.RetryDelayMultiplier,
                SettingKeys.Translation.LanguageCodeFormat
            ]);
            _model = ResolveModel(settings[SettingKeys.Translation.LocalAi.Model]);
            _endpoint = settings[SettingKeys.Translation.LocalAi.Endpoint];
            _chatRequestTemplate = !string.IsNullOrEmpty(settings[SettingKeys.Translation.LocalAi.ChatRequestTemplate])
                ? settings[SettingKeys.Translation.LocalAi.ChatRequestTemplate]
                : _requestTemplateService.GetDefaultTemplate(SettingKeys.Translation.LocalAi.ChatRequestTemplate);
            _generateRequestTemplate = !string.IsNullOrEmpty(settings[SettingKeys.Translation.LocalAi.GenerateRequestTemplate])
                ? settings[SettingKeys.Translation.LocalAi.GenerateRequestTemplate]
                : _requestTemplateService.GetDefaultTemplate(SettingKeys.Translation.LocalAi.GenerateRequestTemplate);

            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                // A missing endpoint is provider-wide: every model of this provider shares it.
                throw new TranslationConfigurationException(
                    "Local AI service requires an endpoint address to be configured in settings.");
            }

            if (string.IsNullOrWhiteSpace(_model))
            {
                // A missing model only fails this candidate. A later row may carry an override.
                throw new TranslationException(
                    "Local AI service requires a model name to be configured in settings or on this candidate.");
            }

            SetLanguageReplacements(sourceLanguage, targetLanguage, settings[SettingKeys.Translation.LanguageCodeFormat]);
            _prompt = settings[SettingKeys.Translation.AiPrompt];
            _userPrompt = settings[SettingKeys.Translation.AiUserPrompt];
            _aiThinking = AiThinking.Parse(settings.GetValueOrDefault(SettingKeys.Translation.AiThinking));
            _maxTokens = AiMaxTokens.Parse(settings.GetValueOrDefault(SettingKeys.Translation.AiMaxTokens));
            _proofreadPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadPrompt);
            _proofreadUserPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadUserPrompt);
            settings.TryGetValue(SettingKeys.Translation.LocalAi.Protocol, out var protocolSetting);
            var resolved = LocalAiEndpointResolver.ResolveCompletionEndpoint(
                _endpoint,
                LocalAiProtocolParser.Parse(protocolSetting));
            _endpoint = resolved.Url;
            _isChatEndpoint = resolved.IsChat;

            var requestTimeoutMinutes = int.TryParse(settings[SettingKeys.Translation.RequestTimeout],
                out var timeOut)
                ? timeOut
                : 5;
            _requestTimeout = TimeSpan.FromMinutes(requestTimeoutMinutes);

            var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.LocalAi.ApiKey);
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            if (string.IsNullOrEmpty(_apiKey) && _isChatEndpoint)
            {
                _logger.LogWarning(
                    "LocalAI API key is not configured. Authenticated OpenAI-compatible gateways will reject chat completions.");
            }

            _maxRetries = int.TryParse(settings[SettingKeys.Translation.MaxRetries], out var maxRetries) 
                ? maxRetries 
                : 5;
            var retryDelaySeconds = int.TryParse(settings[SettingKeys.Translation.RetryDelay], out var delaySeconds) 
                ? delaySeconds 
                : 1;
            _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
            _retryDelayMultiplier = int.TryParse(settings[SettingKeys.Translation.RetryDelayMultiplier], out var multiplier) 
                ? multiplier 
                : 2;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        var replacements = GetReplacements(_model!, text, contextLinesBefore, contextLinesAfter);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await TranslationRequestTimeout.ExecuteAsync(
                    _requestTimeout,
                    token => CompleteWithLocalAiApi(replacements, token),
                    cancellationToken);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", text);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "Transient LocalAI error. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (TranslationException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during translation attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during translation.", ex);
            }
        }

        throw new TranslationException("Translation failed after maximum retry attempts.");
    }

    /// <inheritdoc />
    public async Task<string> ProofreadAsync(
        string sourceText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        var replacements = GetProofreadReplacements(_model!, sourceText, translatedText);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await TranslationRequestTimeout.ExecuteAsync(
                    _requestTimeout,
                    token => CompleteWithLocalAiApi(replacements, token),
                    cancellationToken);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", translatedText);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "Transient LocalAI error. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (TranslationException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during proofread attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during proofread.", ex);
            }
        }

        throw new TranslationException("Proofread failed after maximum retry attempts.");
    }

    private async Task<string> CompleteWithLocalAiApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        return _isChatEndpoint
            ? await TranslateWithChatApi(replacements, cancellationToken)
            : await TranslateWithGenerateApi(replacements, cancellationToken);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string jsonBody, CancellationToken cancellationToken)
    {
        using var request = OpenAiCompatibleHttp.CreateJsonPost(_endpoint!, jsonBody, _apiKey, _sessionId);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Translates a batch of subtitles in a single API call using structured outputs fallback
    /// Since LocalAI may not support structured outputs, we'll attempt structured format first,
    /// then fall back to regular parsing if needed. Responses that cannot be parsed are retried
    /// using the configured retry settings, as local models occasionally emit malformed JSON.
    /// </summary>
    /// <param name="subtitleBatch">List of subtitles with position and content</param>
    /// <param name="sourceLanguage">Source language code</param>
    /// <param name="targetLanguage">Target language code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary mapping position to translated content</returns>
    public async Task<Dictionary<int, string>> TranslateBatchAsync(
        List<BatchSubtitleItem> subtitleBatch,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await TranslationRequestTimeout.ExecuteAsync(
                    _requestTimeout,
                    token => TranslateBatchWithLocalAiApi(subtitleBatch, token),
                    cancellationToken);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted for batch translation after a retryable error");
                    throw new TranslationException("Retry limit reached after repeated retryable errors.", ex);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received a retryable error. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", delay, attempt, _maxRetries);
            }
            catch (TranslationParseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted for batch translation, the model kept returning an unparsable response");
                    throw new TranslationException("Retry limit reached after unparsable response.", ex);
                }

                _logger.LogWarning(
                    "{ServiceName} returned an unparsable response. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", delay, attempt, _maxRetries);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);
            }
            catch (TranslationException)
            {
                // Keep the classification (rejected credentials, unusable configuration) so the
                // translation chain can skip the provider instead of wrapping it into a generic failure.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during batch translation attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during batch translation.", ex);
            }
        }

        throw new TranslationException("Batch translation failed after maximum retry attempts.");
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithLocalAiApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        if (!_isChatEndpoint)
        {
            return await TranslateBatchWithGenerateApi(subtitleBatch, cancellationToken);
        }

        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        return await _chatClient.CompleteBatchAsync(
            _endpoint!,
            _apiKey,
            _chatRequestTemplate!,
            replacements,
            _aiThinking,
            _maxTokens,
            cancellationToken);
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithGenerateApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        replacements["systemPrompt"] +=
            "\n\nPlease return the response as a JSON array with objects containing 'position' and 'line' fields. Example: [{\"position\": 1, \"line\": \"translated text\"}]";
        var bodyJson = _requestTemplateService.BuildRequestBody(_generateRequestTemplate!, replacements);
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, new Dictionary<string, object?>
        {
            ["stream"] = false
        });

        var response = await PostJsonAsync(bodyJson, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI generate API batch request failed with status {StatusCode}: {ResponseContent}",
                response.StatusCode, responseContent);
            throw HttpFailure.FromResponse(
                "LocalAI",
                response,
                $"LocalAI generate API batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var generateResponse = JsonSerializer.Deserialize<GenerateResponse>(responseBody);

        if (generateResponse == null || string.IsNullOrEmpty(generateResponse.Response))
        {
            throw new TranslationException("Invalid or empty response from generate API.");
        }

        return BatchJsonParser.ParseJsonArray(generateResponse.Response, _logger);
    }

    private async Task<string> TranslateWithGenerateApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var bodyJson = _requestTemplateService.BuildRequestBody(_generateRequestTemplate!, replacements);
        var response = await PostJsonAsync(bodyJson, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI generate API request failed with status {StatusCode}: {ResponseContent}",
                response.StatusCode, responseContent);
            throw HttpFailure.FromResponse(
                "LocalAI",
                response,
                $"LocalAI generate API request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var generateResponse = JsonSerializer.Deserialize<GenerateResponse>(responseBody);

        if (generateResponse == null || string.IsNullOrEmpty(generateResponse.Response))
        {
            throw new TranslationException("Invalid or empty response from generate API.");
        }

        return generateResponse.Response;
    }

    private async Task<string> TranslateWithChatApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        return await _chatClient.CompleteAsync(
            _endpoint!,
            _apiKey,
            _chatRequestTemplate!,
            replacements,
            _aiThinking,
            _maxTokens,
            cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<ModelsResponse> GetModels()
    {
        var endpoint = await _settings.GetSetting(SettingKeys.Translation.LocalAi.Endpoint);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ModelsResponse
            {
                Message = "LocalAI endpoint is not configured."
            };
        }

        var protocol = await _settings.GetSetting(SettingKeys.Translation.LocalAi.Protocol);
        var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.LocalAi.ApiKey);
        return await LocalAiModelCatalog.FetchAsync(_httpClient, endpoint, protocol, apiKey);
    }
}