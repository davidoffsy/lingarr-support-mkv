using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Core;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation;
using Lingarr.Server.Services.Translation.LocalAi;
using Lingarr.Server.Services.Translation.OpenAiCompatible;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class LocalAiServiceTests
{
    private const string GenerateEndpoint = "http://localhost:11434/api/generate";
    private const string ChatEndpoint = "http://localhost:11434/v1/chat/completions";

    /// <summary>
    /// A response where the model left an unescaped quote inside a translated line, the failure
    /// mode reported in issue #456 for small local models such as gemma4:e4b.
    /// </summary>
    private const string MalformedJson =
        "[{\"position\":1,\"line\":\"Hola\"},{\"position\":2,\"line\":\"Él dijo \"adiós\" y se fue\"}]";

    private const string ValidJson =
        "[{\"position\":1,\"line\":\"Hola\"},{\"position\":2,\"line\":\"Mundo\"}]";

    private readonly Mock<ISettingService> _settingsMock;
    private readonly Mock<ILogger<LocalAiService>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly LocalAiService _service;

    public LocalAiServiceTests()
    {
        _settingsMock = new Mock<ISettingService>();
        _loggerMock = new Mock<ILogger<LocalAiService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();

        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);

        _service = new LocalAiService(
            _settingsMock.Object,
            new HttpClient(_httpMessageHandlerMock.Object),
            _loggerMock.Object,
            new LanguageCodeService(),
            new RequestTemplateService());
    }

    [Fact]
    public async Task GetLanguagePair_UnknownToZhCn_IsAccepted()
    {
        var pair = await _service.GetLanguagePair("unknown", "zh-CN", CancellationToken.None);

        Assert.NotNull(pair);
        Assert.Equal("unknown", pair.Source);
        Assert.Equal("zh-CN", pair.Target);
        Assert.Equal(MatchTier.Exact, pair.Tier);
    }

    [Fact]
    public async Task TranslateBatchAsync_GenerateApi_NumberedList_ParsesWithoutRetry()
    {
        UseSettings(GenerateEndpoint);
        SetupResponse(() => GenerateResponse("1. Hola\n2. Mundo"));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateBatchAsync_ShouldRetry_WhenGenerateApiReturnsMalformedJson()
    {
        // Arrange
        UseSettings(GenerateEndpoint);
        SetupResponseSequence(GenerateResponse(MalformedJson), GenerateResponse(ValidJson));

        // Act
        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
        VerifyRequestsSent(2);
        VerifyWarningLogged("returned an unparsable response", Times.Once());
    }

    [Fact]
    public async Task TranslateBatchAsync_ShouldThrow_WhenGenerateApiKeepsReturningMalformedJson()
    {
        // Arrange
        UseSettings(GenerateEndpoint);
        SetupResponse(() => GenerateResponse(MalformedJson));

        // Act
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        // Assert
        Assert.Contains("Retry limit reached", exception.Message);
        Assert.IsType<TranslationParseException>(exception.InnerException);
        VerifyRequestsSent(3); // MaxRetries
    }

    [Fact]
    public async Task TranslateBatchAsync_ShouldRetry_WhenChatApiFallbackReturnsMalformedJson()
    {
        // Arrange
        UseSettings(ChatEndpoint);

        // The chat endpoint tries structured output first and falls back to plain JSON parsing,
        // so a failing attempt sends two requests before the retry kicks in.
        SetupResponseSequence(
            ChatResponse(MalformedJson),
            ChatResponse(MalformedJson),
            ChatResponse("{\"translations\":" + ValidJson + "}"));

        // Act
        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Mundo", result[2]);
        VerifyRequestsSent(3);
        VerifyWarningLogged("returned an unparsable response", Times.Once());
    }

    [Fact]
    public async Task TranslateBatchAsync_ShouldNotRetry_WhenRequestFailsWithNonRetryableStatus()
    {
        // Arrange
        UseSettings(GenerateEndpoint);
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.BadRequest,
            Content = new StringContent("{\"error\":\"model not found\"}", Encoding.UTF8, "application/json")
        });

        // Act
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        // Assert
        // The provider specific failure is kept instead of being wrapped into a generic one, so the
        // status code stays visible in logs and the chain sees the real failure classification.
        Assert.Contains("failed with status BadRequest", exception.Message);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_Chat_MissingThinkingSetting_OmitsThinking()
    {
        UseSettings(ChatEndpoint);
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertNoThinking(bodies[0]);
    }

    [Fact]
    public async Task TranslateAsync_Chat_ThinkingOff_InjectsDisabled()
    {
        UseSettings(ChatEndpoint, aiThinking: "off");
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertThinkingType(bodies[0], "disabled");
    }

    [Fact]
    public async Task TranslateAsync_Chat_ThinkingAuto_OmitsThinking()
    {
        UseSettings(ChatEndpoint, aiThinking: "auto");
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertNoThinking(bodies[0]);
    }

    [Fact]
    public async Task TranslateAsync_Chat_ThinkingOn_InjectsEnabled()
    {
        UseSettings(ChatEndpoint, aiThinking: "on");
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertThinkingType(bodies[0], "enabled");
    }

    [Fact]
    public async Task TranslateAsync_Generate_DoesNotInjectThinking()
    {
        UseSettings(GenerateEndpoint, aiThinking: "off");
        var bodies = CaptureRequestBodies(() => GenerateResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertNoThinking(bodies[0]);
        AssertNoMaxTokens(bodies[0]);
    }

    [Fact]
    public async Task TranslateAsync_Chat_MissingMaxTokensSetting_OmitsField()
    {
        UseSettings(ChatEndpoint);
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertNoMaxTokens(bodies[0]);
        AssertNoMaxCompletionTokens(bodies[0]);
        AssertNoThinking(bodies[0]);
    }

    [Fact]
    public async Task TranslateAsync_Chat_MaxTokensZero_OmitsField()
    {
        UseSettings(ChatEndpoint, aiMaxTokens: "0");
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertNoMaxTokens(bodies[0]);
        AssertNoMaxCompletionTokens(bodies[0]);
        AssertNoThinking(bodies[0]);
    }

    [Fact]
    public async Task TranslateAsync_Chat_MaxTokensPositive_AddsMaxTokensOnly()
    {
        UseSettings(ChatEndpoint, aiThinking: "auto", aiMaxTokens: "8192");
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertMaxTokens(bodies[0], 8192);
        AssertNoMaxCompletionTokens(bodies[0]);
        AssertNoThinking(bodies[0]);
    }

    [Fact]
    public async Task TranslateAsync_Generate_DoesNotInjectMaxTokens()
    {
        UseSettings(GenerateEndpoint, aiMaxTokens: "8192");
        var bodies = CaptureRequestBodies(() => GenerateResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        AssertNoMaxTokens(bodies[0]);
        AssertNoMaxCompletionTokens(bodies[0]);
    }

    [Fact]
    public async Task TranslateBatchAsync_ChatStructured_IncludesThinkingAndResponseFormat()
    {
        UseSettings(ChatEndpoint, aiThinking: "off");
        var bodies = CaptureRequestBodies(() => ChatResponse("{\"translations\":" + ValidJson + "}"));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal("Hola", result[1]);
        Assert.Single(bodies);
        AssertThinkingType(bodies[0], "disabled");
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.Equal("json_schema", document.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TranslateAsync_OpenAiChatProtocol_PostsToChatCompletions()
    {
        UseSettings("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        SetupResponse(() => ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri("https://opencode.ai/zen/go/v1/chat/completions", Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_OpenCodeGo_SendsUserAgentAndStableSession()
    {
        UseSettings("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        var captured = CaptureRequests(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);
        await _service.TranslateAsync("World", "en", "es", null, null, CancellationToken.None);

        Assert.Equal(2, captured.Count);
        var firstSession = HeaderValue(captured[0], OpenCodeGoHeaders.SessionHeaderName);
        var secondSession = HeaderValue(captured[1], OpenCodeGoHeaders.SessionHeaderName);
        Assert.False(string.IsNullOrWhiteSpace(firstSession));
        Assert.Equal(firstSession, secondSession);
        Assert.Equal($"{LingarrVersion.Name}/{LingarrVersion.Number}", HeaderValue(captured[0], "User-Agent"));
    }

    [Fact]
    public async Task TranslateAsync_Ollama_DoesNotSendOpenCodeSession()
    {
        UseSettings(GenerateEndpoint);
        var captured = CaptureRequests(() => GenerateResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        Assert.Single(captured);
        Assert.Null(HeaderValue(captured[0], OpenCodeGoHeaders.SessionHeaderName));
        Assert.Equal($"{LingarrVersion.Name}/{LingarrVersion.Number}", HeaderValue(captured[0], "User-Agent"));
    }

    [Fact]
    public async Task GetModels_OpenCodeGo_SendsSessionHeader()
    {
        SetupGetSetting("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        var captured = CaptureRequests(() => JsonResponse(new { data = new[] { new { id = "glm-5.2" } } }));

        var result = await _service.GetModels();

        Assert.Null(result.Message);
        Assert.Single(captured);
        Assert.False(string.IsNullOrWhiteSpace(HeaderValue(captured[0], OpenCodeGoHeaders.SessionHeaderName)));
        Assert.Equal($"{LingarrVersion.Name}/{LingarrVersion.Number}", HeaderValue(captured[0], "User-Agent"));
    }

    [Fact]
    public async Task GetModels_Ollama_DoesNotSendOpenCodeSession()
    {
        SetupGetSetting("http://localhost:11434", SettingKeys.Translation.LocalAi.Protocols.OllamaGenerate);
        var captured = CaptureRequests(() => JsonResponse(new { models = new[] { new { name = "llama3:latest" } } }));

        var result = await _service.GetModels();

        Assert.Equal("llama3:latest", result.Options[0].Value);
        Assert.Single(captured);
        Assert.Null(HeaderValue(captured[0], OpenCodeGoHeaders.SessionHeaderName));
        Assert.Equal($"{LingarrVersion.Name}/{LingarrVersion.Number}", HeaderValue(captured[0], "User-Agent"));
    }

    [Fact]
    public async Task TranslateAsync_WithApiKey_SendsBearerAuthorization()
    {
        const string apiKey = "sk-test-key";
        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.LocalAi.ApiKey))
            .ReturnsAsync("  " + apiKey + " \n");
        UseSettings("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        SetupResponse(() => ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyAuthorization("Bearer " + apiKey, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_Unauthorized_DoesNotRetry()
    {
        UseSettings("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.Unauthorized,
            Content = new StringContent(
                "{\"type\":\"error\",\"error\":{\"type\":\"AuthError\",\"message\":\"Missing API key.\"}}",
                Encoding.UTF8,
                "application/json")
        });

        // A rejected credential is reported as an authentication failure, which lets the translation
        // chain skip the remaining models of this provider instead of failing them one by one.
        var exception = await Assert.ThrowsAsync<TranslationAuthenticationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        Assert.Contains("Unauthorized", exception.Message);
        Assert.Contains("Missing API key", exception.Message);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_RequestTimeout_IsRetried()
    {
        UseSettings(GenerateEndpoint);
        _httpMessageHandlerMock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException())
            .ReturnsAsync(GenerateResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateAsync_RequestTimeout_RetriesUntilLimit()
    {
        UseSettings(GenerateEndpoint);
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());

        await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        VerifyRequestsSent(3);
    }

    [Fact]
    public async Task TranslateAsync_CallerCancellation_DoesNotRetry()
    {
        UseSettings(GenerateEndpoint);
        using var cts = new CancellationTokenSource();
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new TaskCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, cts.Token));

        VerifyRequestsSent(1);
    }

    [Fact]
    public void Constructor_DisablesHttpClientTimeout_WithoutChangingOtherClients()
    {
        using var other = new HttpClient();
        var originalOther = other.Timeout;
        using var client = new HttpClient();

        _ = new LocalAiService(
            _settingsMock.Object,
            client,
            _loggerMock.Object,
            new LanguageCodeService(),
            new RequestTemplateService());

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(originalOther, other.Timeout);
    }

    [Fact]
    public async Task TranslateAsync_AutoBareHost_PostsToGenerate()
    {
        UseSettings("http://localhost:11434", SettingKeys.Translation.LocalAi.Protocols.Auto);
        SetupResponse(() => GenerateResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri("http://localhost:11434/api/generate", Times.Once());
    }

    [Fact]
    public async Task GetModels_OpenAiChat_ReturnsCatalogue()
    {
        SetupGetSetting("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        SetupResponse(() => JsonResponse(new { data = new[] { new { id = "omen-alpha" }, new { id = "glm-5.2" } } }));

        var result = await _service.GetModels();

        Assert.Null(result.Message);
        Assert.Equal(2, result.Options.Count);
        Assert.Contains(result.Options, option => option.Value == "omen-alpha");
        VerifyRequestUri("https://opencode.ai/zen/go/v1/models", Times.Once());
    }

    [Fact]
    public async Task GetModels_WithApiKey_SendsBearerAuthorization()
    {
        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.LocalAi.ApiKey))
            .ReturnsAsync("sk-test-key");
        SetupGetSetting("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        SetupResponse(() => JsonResponse(new { data = new[] { new { id = "hy4-preview" } } }));

        var result = await _service.GetModels();

        Assert.Equal("hy4-preview", result.Options[0].Value);
        VerifyAuthorization("Bearer sk-test-key", Times.Once());
    }

    [Fact]
    public async Task GetModels_OllamaGenerate_ReturnsTags()
    {
        SetupGetSetting("http://localhost:11434", SettingKeys.Translation.LocalAi.Protocols.OllamaGenerate);
        SetupResponse(() => JsonResponse(new { models = new[] { new { name = "llama3:latest" } } }));

        var result = await _service.GetModels();

        Assert.Equal("llama3:latest", result.Options[0].Value);
        VerifyRequestUri("http://localhost:11434/api/tags", Times.Once());
    }

    [Fact]
    public async Task GetModels_HtmlNotFound_ReturnsFriendlyMessage()
    {
        SetupGetSetting("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.Auto);
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.NotFound,
            Content = new StringContent("<html>404 - Page Not Found</html>", Encoding.UTF8, "text/html")
        });

        var result = await _service.GetModels();

        Assert.NotNull(result.Message);
        Assert.Contains("OpenAI Chat Completions", result.Message);
        Assert.DoesNotContain("<html>", result.Message);
        Assert.Empty(result.Options);
    }

    [Fact]
    public async Task GetModels_OpenAiChat_EmptyCatalogue_IsSuccess()
    {
        SetupGetSetting("https://opencode.ai/zen/go/v1", SettingKeys.Translation.LocalAi.Protocols.OpenAiChat);
        SetupResponse(() => JsonResponse(new { data = Array.Empty<object>() }));

        var result = await _service.GetModels();

        Assert.Null(result.Message);
        Assert.Empty(result.Options);
    }

    private static List<BatchSubtitleItem> Batch() =>
    [
        new() { Position = 1, Line = "Hello" },
        new() { Position = 2, Line = "World" }
    ];

    private static HttpResponseMessage GenerateResponse(string translatedJson) =>
        JsonResponse(new { model = "gemma4:e4b", response = translatedJson, done = true });

    private static HttpResponseMessage ChatResponse(string translatedJson) =>
        JsonResponse(new { choices = new[] { new { message = new { content = translatedJson } } } });

    private static HttpResponseMessage JsonResponse(object body) => new()
    {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private void UseSettings(
        string endpoint,
        string? protocol = null,
        string? aiThinking = null,
        string? aiMaxTokens = null)
    {
        var settings = new Dictionary<string, string>
        {
            { SettingKeys.Translation.LocalAi.Model, "gemma4:e4b" },
            { SettingKeys.Translation.LocalAi.Endpoint, endpoint },
            { SettingKeys.Translation.LocalAi.ChatRequestTemplate, "" },
            { SettingKeys.Translation.LocalAi.GenerateRequestTemplate, "" },
            { SettingKeys.Translation.AiPrompt, "Translate from {sourceLanguage} to {targetLanguage}" },
            { SettingKeys.Translation.AiUserPrompt, "{lineToTranslate}" },
            { SettingKeys.Translation.RequestTimeout, "5" },
            { SettingKeys.Translation.MaxRetries, "3" },
            { SettingKeys.Translation.RetryDelay, "0" }, // No delay to keep the tests fast
            { SettingKeys.Translation.RetryDelayMultiplier, "1" },
            { SettingKeys.Translation.LanguageCodeFormat, "false" }
        };

        if (!string.IsNullOrEmpty(protocol))
        {
            settings[SettingKeys.Translation.LocalAi.Protocol] = protocol;
        }

        if (aiThinking != null)
        {
            settings[SettingKeys.Translation.AiThinking] = aiThinking;
        }

        if (aiMaxTokens != null)
        {
            settings[SettingKeys.Translation.AiMaxTokens] = aiMaxTokens;
        }

        _settingsMock.Setup(settingService => settingService.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(settings);
    }

    private void SetupGetSetting(string endpoint, string protocol)
    {
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.LocalAi.Endpoint))
            .ReturnsAsync(endpoint);
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.LocalAi.Protocol))
            .ReturnsAsync(protocol);
    }

    /// <summary>
    /// Answers every request with a freshly built response, so an attempt never reads content
    /// that a previous attempt already consumed.
    /// </summary>
    private void SetupResponse(Func<HttpResponseMessage> responseFactory)
    {
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(responseFactory()));
    }

    private List<HttpRequestMessage> CaptureRequests(Func<HttpResponseMessage> responseFactory)
    {
        var captured = new List<HttpRequestMessage>();
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => captured.Add(request))
            .Returns(() => Task.FromResult(responseFactory()));
        return captured;
    }

    private List<string> CaptureRequestBodies(params Func<HttpResponseMessage>[] responseFactories)
    {
        var captured = new List<string>();
        var index = 0;
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                captured.Add(body);
            })
            .Returns(() =>
            {
                var factory = responseFactories[Math.Min(index, responseFactories.Length - 1)];
                index++;
                return Task.FromResult(factory());
            });
        return captured;
    }

    private static void AssertThinkingType(string json, string expectedType)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expectedType, document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    private static void AssertNoThinking(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("thinking", out _));
    }

    private static void AssertMaxTokens(string json, int expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("max_tokens").GetInt32());
    }

    private static void AssertNoMaxTokens(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("max_tokens", out _));
    }

    private static void AssertMaxCompletionTokens(string json, int expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    private static void AssertNoMaxCompletionTokens(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    private static string? HeaderValue(HttpRequestMessage request, string name)
    {
        if (!request.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            return value;
        }

        return null;
    }

    private void SetupResponseSequence(params HttpResponseMessage[] responses)
    {
        var sequence = _httpMessageHandlerMock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var response in responses)
        {
            sequence = sequence.ReturnsAsync(response);
        }
    }

    private void VerifyRequestsSent(int count)
    {
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(count),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    private void VerifyRequestUri(string expectedUri, Times times)
    {
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            times,
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.ToString() == expectedUri),
            ItExpr.IsAny<CancellationToken>());
    }

    private void VerifyAuthorization(string expectedHeader, Times times)
    {
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            times,
            ItExpr.Is<HttpRequestMessage>(request => HasAuthorization(request, expectedHeader)),
            ItExpr.IsAny<CancellationToken>());
    }

    private static bool HasAuthorization(HttpRequestMessage request, string expectedHeader)
    {
        if (request.Headers.TryGetValues("Authorization", out var values))
        {
            foreach (var value in values)
            {
                if (value == expectedHeader)
                {
                    return true;
                }
            }
        }

        return request.Headers.Authorization?.ToString() == expectedHeader;
    }

    private void VerifyWarningLogged(string message, Times times)
    {
        _loggerMock.Verify(
            logger => logger.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains(message)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }
}
