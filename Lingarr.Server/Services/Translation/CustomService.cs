using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services.Translation.AnthropicMessages;
using Lingarr.Server.Services.Translation.Base;
using Lingarr.Server.Services.Translation.OpenAiCompatible;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Independent custom provider: a user-supplied gateway spoken over a non-streaming protocol
/// chosen in <c>custom_protocol</c> (OpenAI Chat Completions, OpenAI Responses, or Anthropic
/// Messages). Keeps its own <c>custom_*</c> settings (endpoint, encrypted key, model, protocol,
/// per-protocol request template) so it can be configured next to LocalAI and official Anthropic
/// without sharing identity, address or credentials.
/// </summary>
public class CustomService : BaseLanguageService, ITranslationService, IBatchTranslationService, IProofreadService, IModelOverridable
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private readonly OpenAiChatCompletionsClient _chatClient;
    private readonly OpenAiResponsesClient _responsesClient;
    private readonly AnthropicMessagesClient _messagesClient;
    private CustomProtocol _protocol;
    private string? _model;
    private string? _endpoint;
    private string? _apiKey;
    private string? _anthropicVersion;
    private string? _requestTemplate;
    private AiThinkingMode _aiThinking;
    private int _maxTokens;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // retry settings
    private int _maxRetries;
    private TimeSpan _retryDelay;
    private int _retryDelayMultiplier;
    private TimeSpan _requestTimeout;
    private readonly string _sessionId = OpenCodeGoHeaders.NewSessionId();

    /// <inheritdoc />
    public override string? ModelName => _model;

    /// <inheritdoc />
    public void OverrideModel(string model) => SetModelOverride(model);

    public CustomService(
        ISettingService settings,
        HttpClient httpClient,
        ILogger<CustomService> logger,
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
            "Custom",
            _sessionId);
        _responsesClient = new OpenAiResponsesClient(
            httpClient,
            requestTemplateService,
            logger,
            "Custom",
            _sessionId);
        _messagesClient = new AnthropicMessagesClient(
            httpClient,
            requestTemplateService,
            logger,
            "Custom",
            _sessionId);
    }

    /// <summary>
    /// Initializes the service from the <c>custom_*</c> settings. Thread-safe; runs once.
    /// </summary>
    private async Task InitializeAsync(string sourceLanguage, string targetLanguage)
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var settings = await _settings.GetSettings([
                SettingKeys.Translation.Custom.Model,
                SettingKeys.Translation.Custom.Endpoint,
                SettingKeys.Translation.Custom.Protocol,
                SettingKeys.Translation.Custom.ChatRequestTemplate,
                SettingKeys.Translation.Custom.ResponsesRequestTemplate,
                SettingKeys.Translation.Custom.MessagesRequestTemplate,
                SettingKeys.Translation.Custom.AnthropicVersion,
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

            var protocolSetting = settings.GetValueOrDefault(SettingKeys.Translation.Custom.Protocol);
            if (!CustomProtocols.TryParse(protocolSetting, out _protocol))
            {
                // An unknown protocol is a configuration error, not something to guess: the request
                // shape of the wrong protocol would fail on every line of the provider.
                throw new TranslationConfigurationException(
                    $"Custom protocol '{protocolSetting}' is not supported. Set {SettingKeys.Translation.Custom.Protocol} to {CustomProtocols.SupportedValues}.");
            }

            // Each protocol reads its own template key, so editing one never changes the other.
            var templateKey = _protocol switch
            {
                CustomProtocol.OpenAiResponses => SettingKeys.Translation.Custom.ResponsesRequestTemplate,
                CustomProtocol.AnthropicMessages => SettingKeys.Translation.Custom.MessagesRequestTemplate,
                CustomProtocol.OpenAiChat => SettingKeys.Translation.Custom.ChatRequestTemplate
            };

            _model = ResolveModel(settings.GetValueOrDefault(SettingKeys.Translation.Custom.Model));
            var configuredEndpoint = settings.GetValueOrDefault(SettingKeys.Translation.Custom.Endpoint);
            _requestTemplate = !string.IsNullOrEmpty(settings.GetValueOrDefault(templateKey))
                ? settings[templateKey]
                : _requestTemplateService.GetDefaultTemplate(templateKey);

            if (string.IsNullOrWhiteSpace(configuredEndpoint))
            {
                // A missing endpoint is provider-wide: every model of this provider shares it.
                throw new TranslationConfigurationException(
                    "Custom service requires an endpoint address to be configured in settings.");
            }

            if (string.IsNullOrWhiteSpace(_model))
            {
                // A missing model only fails this candidate. A later row may carry an override.
                throw new TranslationException(
                    "Custom service requires a model name to be configured in settings or on this candidate.");
            }

            // The protocol is configured explicitly, so only that protocol's path is built; known
            // paths of the other protocol are still recognised as API paths and replaced.
            try
            {
                _endpoint = _protocol switch
                {
                    CustomProtocol.OpenAiResponses => OpenAiCompatibleEndpoint.ResolveResponsesUrl(configuredEndpoint),
                    CustomProtocol.AnthropicMessages => OpenAiCompatibleEndpoint.ResolveMessagesUrl(configuredEndpoint),
                    CustomProtocol.OpenAiChat => OpenAiCompatibleEndpoint.ResolveChatCompletionsUrl(configuredEndpoint)
                };
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException)
            {
                throw new TranslationConfigurationException(
                    $"Custom service endpoint '{configuredEndpoint}' is not a usable URL.", ex);
            }

            SetLanguageReplacements(sourceLanguage, targetLanguage,
                settings.GetValueOrDefault(SettingKeys.Translation.LanguageCodeFormat) ?? string.Empty);
            _prompt = settings.GetValueOrDefault(SettingKeys.Translation.AiPrompt);
            _userPrompt = settings.GetValueOrDefault(SettingKeys.Translation.AiUserPrompt);
            _aiThinking = AiThinking.Parse(settings.GetValueOrDefault(SettingKeys.Translation.AiThinking));
            _maxTokens = AiMaxTokens.Parse(settings.GetValueOrDefault(SettingKeys.Translation.AiMaxTokens));
            _proofreadPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadPrompt);
            _proofreadUserPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadUserPrompt);

            var requestTimeoutMinutes = int.TryParse(
                settings.GetValueOrDefault(SettingKeys.Translation.RequestTimeout),
                out var timeOut)
                ? timeOut
                : 5;
            _requestTimeout = TimeSpan.FromMinutes(requestTimeoutMinutes);

            if (_protocol == CustomProtocol.AnthropicMessages)
            {
                var version = settings.GetValueOrDefault(SettingKeys.Translation.Custom.AnthropicVersion);
                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new TranslationConfigurationException(
                        $"Custom Anthropic Messages requires {SettingKeys.Translation.Custom.AnthropicVersion} (sent as the anthropic-version header).");
                }

                _anthropicVersion = version.Trim();
            }

            var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.Custom.ApiKey);
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogWarning(
                    _protocol == CustomProtocol.AnthropicMessages
                        ? "Custom API key is not configured. Anthropic Messages gateways that require x-api-key will reject the request."
                        : "Custom API key is not configured. Authenticated OpenAI-compatible gateways will reject the request.");
            }

            _maxRetries = int.TryParse(settings.GetValueOrDefault(SettingKeys.Translation.MaxRetries),
                out var maxRetries)
                ? maxRetries
                : 5;
            var retryDelaySeconds = int.TryParse(settings.GetValueOrDefault(SettingKeys.Translation.RetryDelay),
                out var delaySeconds)
                ? delaySeconds
                : 1;
            _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
            _retryDelayMultiplier = int.TryParse(
                settings.GetValueOrDefault(SettingKeys.Translation.RetryDelayMultiplier),
                out var multiplier)
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
                    token => CompleteOnceAsync(replacements, token),
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
                    "Transient Custom error. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
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
                    token => CompleteOnceAsync(replacements, token),
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
                    "Transient Custom error. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
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

    /// <inheritdoc />
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
                    token => TranslateBatchWithCustomApi(subtitleBatch, token),
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
                    "Custom", delay, attempt, _maxRetries);
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
                    "Custom", delay, attempt, _maxRetries);

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

    private Task<Dictionary<int, string>> TranslateBatchWithCustomApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        return _protocol switch
        {
            CustomProtocol.AnthropicMessages => _messagesClient.CompleteBatchAsync(
                _endpoint!,
                _apiKey,
                _anthropicVersion!,
                _requestTemplate!,
                replacements,
                _maxTokens,
                strict: true,
                cancellationToken),
            CustomProtocol.OpenAiResponses => _responsesClient.CompleteBatchAsync(
                _endpoint!,
                _apiKey,
                _requestTemplate!,
                replacements,
                _aiThinking,
                _maxTokens,
                cancellationToken),
            CustomProtocol.OpenAiChat => _chatClient.CompleteBatchAsync(
                _endpoint!,
                _apiKey,
                _requestTemplate!,
                replacements,
                _aiThinking,
                _maxTokens,
                cancellationToken)
        };
    }

    /// <summary>
    /// One completion on the configured protocol. Messages does not receive <c>ai_thinking</c>:
    /// that setting writes the Chat Completions extension, which a Messages gateway would reject.
    /// </summary>
    private Task<string> CompleteOnceAsync(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken) =>
        _protocol switch
        {
            CustomProtocol.AnthropicMessages => _messagesClient.CompleteAsync(
                _endpoint!,
                _apiKey,
                _anthropicVersion!,
                _requestTemplate!,
                replacements,
                _maxTokens,
                strict: true,
                cancellationToken),
            CustomProtocol.OpenAiResponses => _responsesClient.CompleteAsync(
                _endpoint!,
                _apiKey,
                _requestTemplate!,
                replacements,
                _aiThinking,
                _maxTokens,
                cancellationToken),
            CustomProtocol.OpenAiChat => _chatClient.CompleteAsync(
                _endpoint!,
                _apiKey,
                _requestTemplate!,
                replacements,
                _aiThinking,
                _maxTokens,
                cancellationToken)
        };

    /// <inheritdoc />
    public override async Task<ModelsResponse> GetModels()
    {
        var endpoint = await _settings.GetSetting(SettingKeys.Translation.Custom.Endpoint);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ModelsResponse
            {
                Message = "Custom endpoint is not configured."
            };
        }

        var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.Custom.ApiKey);
        var protocolSetting = await _settings.GetSetting(SettingKeys.Translation.Custom.Protocol);
        if (!CustomProtocols.TryParse(protocolSetting, out var protocol))
        {
            return new ModelsResponse
            {
                Message = $"Custom protocol '{protocolSetting}' is not supported. Set {SettingKeys.Translation.Custom.Protocol} to {CustomProtocols.SupportedValues}."
            };
        }

        if (protocol == CustomProtocol.AnthropicMessages)
        {
            var version = await _settings.GetSetting(SettingKeys.Translation.Custom.AnthropicVersion);
            return await AnthropicMessagesModelCatalog.FetchAsync(_httpClient, endpoint, apiKey, version);
        }

        return await OpenAiCompatibleModelCatalog.FetchAsync(_httpClient, endpoint, apiKey);
    }
}
