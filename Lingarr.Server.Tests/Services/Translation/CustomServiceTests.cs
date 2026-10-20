using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Core;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation;
using Lingarr.Server.Services.Translation.Batch;
using Lingarr.Server.Services.Translation.OpenAiCompatible;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class CustomServiceTests
{
    private const string BaseEndpoint = "http://localhost:8000/v1";
    private const string ChatEndpoint = "http://localhost:8000/v1/chat/completions";
    private const string ResponsesEndpoint = "http://localhost:8000/v1/responses";
    private const string MessagesEndpoint = "http://localhost:8000/v1/messages";

    /// <summary>
    /// A response where the model left an unescaped quote inside a translated line.
    /// </summary>
    private const string MalformedJson =
        "[{\"position\":1,\"line\":\"Hola\"},{\"position\":2,\"line\":\"Él dijo \"adiós\" y se fue\"}]";

    private const string ValidJson =
        "[{\"position\":1,\"line\":\"Hola\"},{\"position\":2,\"line\":\"Mundo\"}]";

    private readonly Mock<ISettingService> _settingsMock;
    private readonly Mock<ILogger<CustomService>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly CustomService _service;

    public CustomServiceTests()
    {
        _settingsMock = new Mock<ISettingService>();
        _loggerMock = new Mock<ILogger<CustomService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();

        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);
        _settingsMock.Setup(settingService => settingService.GetSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);

        _service = new CustomService(
            _settingsMock.Object,
            new HttpClient(_httpMessageHandlerMock.Object),
            _loggerMock.Object,
            new LanguageCodeService(),
            new RequestTemplateService());
    }

    [Fact]
    public async Task TranslateAsync_PostsToChatCompletions()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(ChatEndpoint, Times.Once());
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_FullEndpointPath_IsPreserved()
    {
        UseSettings(ChatEndpoint);
        SetupResponse(() => ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(ChatEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_ThinkingAndMaxTokens_AreInjected()
    {
        UseSettings(BaseEndpoint, aiThinking: "off", aiMaxTokens: "8192");
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.Equal("disabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(8192, document.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task TranslateAsync_WithApiKey_SendsBearerAuthorization()
    {
        const string apiKey = "sk-custom-key";
        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.Custom.ApiKey))
            .ReturnsAsync("  " + apiKey + " \n");
        UseSettings(BaseEndpoint);
        SetupResponse(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        // The custom provider reads its own key, never the LocalAI one.
        _settingsMock.Verify(
            settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.Custom.ApiKey),
            Times.AtLeastOnce());
        VerifyAuthorization("Bearer " + apiKey, Times.Once());
        VerifyHeaderAbsent("x-api-key");
    }

    [Fact]
    public async Task TranslateAsync_OpenCodeGo_SendsStableSession()
    {
        UseSettings("https://opencode.ai/zen/go/v1");
        var captured = CaptureRequests(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);
        await _service.TranslateAsync("World", "en", "es", null, null, CancellationToken.None);

        Assert.Equal(2, captured.Count);
        var firstSession = HeaderValue(captured[0], OpenCodeGoHeaders.SessionHeaderName);
        var secondSession = HeaderValue(captured[1], OpenCodeGoHeaders.SessionHeaderName);
        Assert.False(string.IsNullOrWhiteSpace(firstSession));
        Assert.Equal(firstSession, secondSession);
    }

    [Fact]
    public async Task TranslateAsync_DoesNotReadLocalAiSettings()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => ChatResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        // The custom provider must never fall back to the LocalAI identity.
        _settingsMock.Verify(
            settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.LocalAi.ApiKey),
            Times.Never());
        _settingsMock.Verify(
            settingService => settingService.GetSettings(It.Is<IEnumerable<string>>(keys =>
                keys.Contains(SettingKeys.Translation.Custom.Endpoint)
                && !keys.Contains(SettingKeys.Translation.LocalAi.Endpoint))),
            Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_Unauthorized_DoesNotRetry()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.Unauthorized,
            Content = new StringContent(
                "{\"error\":{\"message\":\"Missing API key.\"}}",
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
    public async Task TranslateAsync_ServiceUnavailable_IsRetried()
    {
        UseSettings(BaseEndpoint);
        SetupResponseSequence(
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Content = new StringContent("{\"error\":\"busy\"}", Encoding.UTF8, "application/json")
            },
            ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateAsync_RequestTimeout_IsRetried()
    {
        UseSettings(BaseEndpoint);
        _httpMessageHandlerMock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException())
            .ReturnsAsync(ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateAsync_RequestTimeout_RetriesUntilLimit()
    {
        UseSettings(BaseEndpoint);
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
        UseSettings(BaseEndpoint);
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

        _ = new CustomService(
            _settingsMock.Object,
            client,
            _loggerMock.Object,
            new LanguageCodeService(),
            new RequestTemplateService());

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(originalOther, other.Timeout);
    }

    [Fact]
    public async Task TranslateAsync_MissingEndpoint_Throws()
    {
        UseSettings(endpoint: string.Empty);
        SetupResponse(() => ChatResponse("Hola"));

        // Reported as a configuration failure so the translation chain skips this provider instead of
        // repeating the same unusable request for every subtitle line.
        await Assert.ThrowsAsync<TranslationConfigurationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task TranslateAsync_MissingModel_Throws()
    {
        UseSettings(BaseEndpoint, model: string.Empty);
        SetupResponse(() => ChatResponse("Hola"));

        // A missing model only fails this candidate; a later chain row may still carry an override.
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        Assert.IsType<TranslationException>(exception);
        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task TranslateAsync_MissingModel_WithOverride_Translates()
    {
        UseSettings(BaseEndpoint, model: string.Empty);
        _service.OverrideModel("override-model");
        SetupResponse(() => ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_NonHttpEndpoint_ThrowsConfigurationException()
    {
        UseSettings("ftp://localhost:8000");
        SetupResponse(() => ChatResponse("Hola"));

        await Assert.ThrowsAsync<TranslationConfigurationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task TranslateBatchAsync_Unauthorized_ReportsAuthenticationFailure()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.Unauthorized,
            Content = new StringContent(
                "{\"error\":{\"message\":\"Missing API key.\"}}",
                Encoding.UTF8,
                "application/json")
        });

        // The batch path has to keep the classification too, otherwise the chain would retry every
        // model of this provider with the same rejected key.
        await Assert.ThrowsAsync<TranslationAuthenticationException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        // A rejected credential is not retried as a second, plain JSON request.
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateBatchAsync_RateLimited_IsRetried()
    {
        UseSettings(BaseEndpoint);
        SetupResponseSequence(
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.TooManyRequests,
                Content = new StringContent("{\"error\":\"slow down\"}", Encoding.UTF8, "application/json")
            },
            // Structured response, so the successful retry needs exactly one request.
            ChatResponse("{\"translations\":" + ValidJson + "}"));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal("Hola", result[1]);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateBatchAsync_BadGateway_IsRetried()
    {
        UseSettings(BaseEndpoint);
        SetupResponseSequence(
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadGateway,
                Content = new StringContent("{\"error\":\"bad gateway\"}", Encoding.UTF8, "application/json")
            },
            ChatResponse("{\"translations\":" + ValidJson + "}"));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal("Hola", result[1]);
        // 502 is retryable: the same structured body is sent again, not a second JSON shape.
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateBatchAsync_StructuredOutput_ParsesWrapper()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => ChatResponse("{\"translations\":" + ValidJson + "}"));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateBatchAsync_JsonSchemaRejectedThreeTimes_DowngradesToJsonObject()
    {
        UseSettings(BaseEndpoint);
        var bodies = CaptureRequestBodiesAndRespond(body =>
            body.Contains("\"json_schema\"", StringComparison.Ordinal)
                ? BadRequestResponse("This response_format type is unavailable now")
                : ChatResponse(body.Contains("\"json_object\"", StringComparison.Ordinal)
                    ? "{\"translations\":" + ValidJson + "}"
                    : ValidJson));

        for (var i = 0; i < 4; i++)
        {
            var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);
            Assert.Equal("Hola", result[1]);
        }

        // Three batches use json_schema and fall back to plain JSON; the fourth uses json_object.
        Assert.Equal(7, bodies.Count);
        using var lastRequest = JsonDocument.Parse(bodies[^1]);
        Assert.Equal(
            "json_object",
            lastRequest.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains(
            "JSON object",
            lastRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain(
            "Return only a JSON array",
            lastRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TranslateBatchAsync_OneJsonSchemaRejection_KeepsJsonSchema()
    {
        UseSettings(BaseEndpoint);
        var rejected = false;
        var bodies = CaptureRequestBodiesAndRespond(body =>
        {
            var usesJsonSchema = body.Contains("\"json_schema\"", StringComparison.Ordinal);
            if (usesJsonSchema && !rejected)
            {
                rejected = true;
                return BadRequestResponse("This response_format type is unavailable now");
            }

            return ChatResponse(usesJsonSchema ? "{\"translations\":" + ValidJson + "}" : ValidJson);
        });

        await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);
        await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        // First batch: schema rejected, plain fallback succeeds. Second batch: schema is tried again,
        // because a single rejection is not enough evidence to downgrade the rest of the run.
        Assert.Equal(3, bodies.Count);
        Assert.Contains("\"json_schema\"", bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateBatchAsync_JsonObjectRejected_DisablesResponseFormat()
    {
        UseSettings(BaseEndpoint);
        var bodies = CaptureRequestBodiesAndRespond(body =>
            body.Contains("\"response_format\"", StringComparison.Ordinal)
                ? BadRequestResponse("This response_format type is unavailable now")
                : ChatResponse(ValidJson));

        for (var i = 0; i < 5; i++)
        {
            var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);
            Assert.Equal("Hola", result[1]);
        }

        // Three json_schema batches, one json_object batch, then a plain request without the field.
        Assert.Equal(9, bodies.Count);
        using var lastRequest = JsonDocument.Parse(bodies[^1]);
        Assert.False(lastRequest.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains(
            "JSON array",
            lastRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TranslateBatchAsync_FallsBackToJsonParsing()
    {
        UseSettings(BaseEndpoint);
        SetupResponseSequence(
            ChatResponse("not json at all"),
            ChatResponse(ValidJson));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Mundo", result[2]);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateBatchAsync_UnparsableResponses_RetryThenFail()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => ChatResponse(MalformedJson));

        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        Assert.Contains("Retry limit reached", exception.Message);
        Assert.IsType<TranslationParseException>(exception.InnerException);
        VerifyRequestsSent(6); // MaxRetries * (structured attempt + JSON fallback)
    }

    [Fact]
    public async Task ProofreadAsync_PostsProofreadPrompt()
    {
        UseSettings(BaseEndpoint);
        var bodies = CaptureRequestBodies(() => ChatResponse("Hola corregido"));

        var result = await _service.ProofreadAsync("Hello", "Hola", "en", "es", CancellationToken.None);

        Assert.Equal("Hola corregido", result);
        Assert.Single(bodies);
        using var document = JsonDocument.Parse(bodies[0]);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Contains("proofread the line", messages[0].GetProperty("content").GetString());
        Assert.Equal("Hola", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetModels_NotConfigured_ReturnsMessage()
    {
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Endpoint))
            .ReturnsAsync(string.Empty);

        var result = await _service.GetModels();

        Assert.NotNull(result.Message);
        Assert.Contains("Custom endpoint is not configured", result.Message);
        Assert.Empty(result.Options);
    }

    [Fact]
    public async Task GetModels_ReturnsCatalogue()
    {
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Endpoint))
            .ReturnsAsync(BaseEndpoint);
        SetupResponse(() => JsonResponse(new { data = new[] { new { id = "my-model" }, new { id = "another-model" } } }));

        var result = await _service.GetModels();

        Assert.Null(result.Message);
        Assert.Equal(2, result.Options.Count);
        VerifyRequestUri("http://localhost:8000/v1/models", Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_ResponsesProtocol_PostsToResponsesEndpoint()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses");
        SetupResponse(() => ResponsesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(ResponsesEndpoint, Times.Once());
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_ResponsesProtocol_FullEndpointPath_IsPreserved()
    {
        UseSettings(ResponsesEndpoint, protocol: "openai_responses");
        SetupResponse(() => ResponsesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(ResponsesEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_ResponsesProtocol_UsesItsOwnTemplate()
    {
        // The chat template stays edited; the Responses request must come from the Responses key.
        UseSettings(
            BaseEndpoint,
            protocol: "openai_responses",
            chatTemplate: "{\"sentinel\":true}",
            responsesTemplate: "");
        var bodies = CaptureRequestBodies(() => ResponsesResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.False(document.RootElement.TryGetProperty("sentinel", out _));
        var input = document.RootElement.GetProperty("input");
        Assert.Equal("system", input[0].GetProperty("role").GetString());
        Assert.Equal("Hello", input[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TranslateAsync_ResponsesProtocol_MaxTokensUsesResponsesField()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses", aiMaxTokens: "4096");
        var bodies = CaptureRequestBodies(() => ResponsesResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.Equal(4096, document.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public async Task TranslateAsync_ResponsesProtocol_ServiceUnavailable_IsRetried()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses");
        SetupResponseSequence(
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Content = new StringContent("{\"error\":\"busy\"}", Encoding.UTF8, "application/json")
            },
            ResponsesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateAsync_UnsupportedProtocol_ThrowsConfigurationError()
    {
        UseSettings(BaseEndpoint, protocol: "not_a_protocol");
        SetupResponse(() => ChatResponse("Hola"));

        var exception = await Assert.ThrowsAsync<TranslationConfigurationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        Assert.Contains("not_a_protocol", exception.Message);
        Assert.Contains("anthropic_messages", exception.Message);
        Assert.Contains("openai_responses", exception.Message);
        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task TranslateAsync_MessagesProtocol_PostsToMessagesEndpoint()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => MessagesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(MessagesEndpoint, Times.Once());
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_MessagesProtocol_FullEndpointPath_IsPreserved()
    {
        UseSettings(MessagesEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => MessagesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(MessagesEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_MessagesProtocol_ChatPath_IsReplaced()
    {
        UseSettings(ChatEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => MessagesResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        VerifyRequestUri(MessagesEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_MessagesProtocol_UsesItsOwnTemplate()
    {
        UseSettings(
            BaseEndpoint,
            protocol: "anthropic_messages",
            chatTemplate: "{\"sentinel\":true}",
            responsesTemplate: "{\"sentinel\":true}",
            messagesTemplate: "");
        var bodies = CaptureRequestBodies(() => MessagesResponse("Hola"));

        await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Single(bodies);
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.False(document.RootElement.TryGetProperty("sentinel", out _));
        Assert.False(document.RootElement.TryGetProperty("input", out _));
        Assert.Contains("Translate from", document.RootElement.GetProperty("system").GetString());
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.DoesNotContain("system", Roles(messages));
    }

    [Fact]
    public async Task TranslateAsync_MessagesProtocol_SendsApiKeyHeader_NotBearer()
    {
        const string apiKey = "sk-ant-custom";
        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.Custom.ApiKey))
            .ReturnsAsync(apiKey);
        UseSettings(BaseEndpoint, protocol: " Anthropic_Messages ", aiThinking: "on", aiMaxTokens: "4096");
        var bodies = CaptureRequestBodies(() => MessagesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyHeader("x-api-key", apiKey, Times.Once());
        VerifyHeader("anthropic-version", "2023-06-01", Times.Once());
        VerifyHeader("User-Agent", $"{LingarrVersion.Name}/{LingarrVersion.Number}", Times.Once());
        VerifyHeaderAbsent("Authorization");
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.Equal(4096, document.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("thinking", out _));
        _settingsMock.Verify(
            settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.Anthropic.ApiKey),
            Times.Never());
    }

    [Fact]
    public async Task TranslateAsync_MessagesProtocol_MissingVersion_IsConfigurationError()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages", anthropicVersion: " ");

        var exception = await Assert.ThrowsAsync<TranslationConfigurationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        Assert.Contains("custom_anthropic_version", exception.Message);
        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task TranslateAsync_MessagesRefusal_IsNotRetried()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => MessagesResponse("No", "refusal"));

        var exception = await Assert.ThrowsAsync<TranslationUnusableResponseException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        Assert.Contains("refused", exception.Message);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_MessagesTruncation_IsNotRetried()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => MessagesResponse("Hol", "max_tokens"));

        await Assert.ThrowsAsync<TranslationUnusableResponseException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateAsync_MessagesUnauthorized_DoesNotRetry()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.Unauthorized,
            Content = new StringContent("{\"error\":\"invalid x-api-key\"}", Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<TranslationAuthenticationException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateBatchAsync_MessagesProtocol_MissingPosition_IsCompletedByFillLoop()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        SetupResponseSequence(
            MessagesToolResponse([(1, "Hola")]),
            MessagesToolResponse([(2, "Mundo")]));

        var filled = await BatchFillLoop.FillAsync(
            _service, Batch(), "en", "es", new Mock<ILogger>().Object, CancellationToken.None);

        Assert.Equal(2, filled.Count);
        Assert.Equal("Hola", filled[1]);
        Assert.Equal("Mundo", filled[2]);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task TranslateBatchAsync_MessagesProtocol_TextJson_DoesNotRequireToolUse()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        SetupResponse(() => MessagesResponse(ValidJson));

        var result = await _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None);

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task ProofreadAsync_MessagesProtocol_PostsMessagesRequest()
    {
        UseSettings(BaseEndpoint, protocol: "anthropic_messages");
        var bodies = CaptureRequestBodies(() => MessagesResponse("Hola corregido"));

        var result = await _service.ProofreadAsync("Hello", "Hola", "en", "es", CancellationToken.None);

        Assert.Equal("Hola corregido", result);
        using var document = JsonDocument.Parse(bodies[0]);
        Assert.Contains("proofread the line", document.RootElement.GetProperty("system").GetString());
        Assert.Equal("Hola", document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        VerifyRequestUri(MessagesEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateBatchAsync_MessagesStreamingTemplate_IsRejectedWithoutRequest()
    {
        const string streamingTemplate =
            "{\"model\":\"{model}\",\"max_tokens\":8192,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"{userMessage}\"}]}";
        UseSettings(BaseEndpoint, protocol: "anthropic_messages", messagesTemplate: streamingTemplate);

        var exception = await Assert.ThrowsAsync<TranslationConfigurationException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        Assert.Contains("\"stream\": true", exception.Message);
        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task GetModels_MessagesProtocol_UsesAnthropicHeaders()
    {
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Endpoint))
            .ReturnsAsync(BaseEndpoint);
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Protocol))
            .ReturnsAsync("anthropic_messages");
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.AnthropicVersion))
            .ReturnsAsync("2023-06-01");
        _settingsMock.Setup(settingService => settingService.GetEncryptedSetting(SettingKeys.Translation.Custom.ApiKey))
            .ReturnsAsync("secret");
        SetupResponse(() => JsonResponse(new { data = new[] { new { id = "claude-a" } } }));

        var result = await _service.GetModels();

        Assert.Null(result.Message);
        Assert.Single(result.Options);
        VerifyRequestUri("http://localhost:8000/v1/models", Times.Once());
        VerifyHeader("x-api-key", "secret", Times.Once());
        VerifyHeader("anthropic-version", "2023-06-01", Times.Once());
        VerifyHeader("User-Agent", $"{LingarrVersion.Name}/{LingarrVersion.Number}", Times.Once());
        VerifyHeaderAbsent("Authorization");
    }

    [Fact]
    public async Task GetModels_UnsupportedProtocol_ReturnsMessage()
    {
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Endpoint))
            .ReturnsAsync(BaseEndpoint);
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Protocol))
            .ReturnsAsync("not_a_protocol");

        var result = await _service.GetModels();

        Assert.NotNull(result.Message);
        Assert.Contains("not_a_protocol", result.Message);
        Assert.Contains("anthropic_messages", result.Message);
        Assert.Empty(result.Options);
        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task GetModels_MessagesProtocol_MissingCatalogue_ReturnsMessage()
    {
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Endpoint))
            .ReturnsAsync(BaseEndpoint);
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.Protocol))
            .ReturnsAsync("anthropic_messages");
        _settingsMock.Setup(settingService => settingService.GetSetting(SettingKeys.Translation.Custom.AnthropicVersion))
            .ReturnsAsync("2023-06-01");
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.NotFound,
            Content = new StringContent("missing", Encoding.UTF8, "text/plain")
        });

        var result = await _service.GetModels();

        Assert.NotNull(result.Message);
        Assert.Contains("Failed to fetch models", result.Message);
        Assert.Empty(result.Options);
    }

    [Fact]
    public async Task TranslateAsync_EmptyProtocol_KeepsChatCompletions()
    {
        // Records written before custom_protocol existed keep the historical behaviour.
        UseSettings(BaseEndpoint, protocol: string.Empty);
        SetupResponse(() => ChatResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(ChatEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateBatchAsync_ResponsesProtocol_MissingPosition_IsCompletedByFillLoop()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses");
        SetupResponseSequence(
            ResponsesResponse("{\"translations\":[{\"position\":1,\"line\":\"Hola\"}]}"),
            ResponsesResponse("{\"translations\":[{\"position\":2,\"line\":\"Mundo\"}]}"));

        var filled = await BatchFillLoop.FillAsync(
            _service, Batch(), "en", "es", new Mock<ILogger>().Object, CancellationToken.None);

        // The model answered position 1 only; the fill loop retried position 2 instead of
        // accepting a half-filled batch or inventing a line.
        Assert.Equal(2, filled.Count);
        Assert.Equal("Hola", filled[1]);
        Assert.Equal("Mundo", filled[2]);
        VerifyRequestsSent(2);
    }

    [Fact]
    public async Task ProofreadAsync_ResponsesProtocol_PostsResponsesRequest()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses");
        var bodies = CaptureRequestBodies(() => ResponsesResponse("Hola corregido"));

        var result = await _service.ProofreadAsync("Hello", "Hola", "en", "es", CancellationToken.None);

        Assert.Equal("Hola corregido", result);
        Assert.Single(bodies);
        using var document = JsonDocument.Parse(bodies[0]);
        var input = document.RootElement.GetProperty("input");
        Assert.Contains("proofread the line", input[0].GetProperty("content").GetString());
        Assert.Equal("Hola", input[1].GetProperty("content").GetString());
        VerifyRequestUri(ResponsesEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_ResponsesProtocol_IgnoresCaseAndWhitespace()
    {
        UseSettings(BaseEndpoint, protocol: " OpenAI_Responses ");
        SetupResponse(() => ResponsesResponse("Hola"));

        var result = await _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None);

        Assert.Equal("Hola", result);
        VerifyRequestUri(ResponsesEndpoint, Times.Once());
    }

    [Fact]
    public async Task TranslateAsync_ResponsesRefusal_IsNotRetried()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses");
        SetupResponse(ResponsesRefusal);

        var exception = await Assert.ThrowsAsync<TranslationUnusableResponseException>(
            () => _service.TranslateAsync("Hello", "en", "es", null, null, CancellationToken.None));

        Assert.Contains("refused", exception.Message);
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateBatchAsync_ResponsesRefusal_IsNotRetried()
    {
        UseSettings(BaseEndpoint, protocol: "openai_responses");
        SetupResponse(ResponsesRefusal);

        await Assert.ThrowsAsync<TranslationUnusableResponseException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        // A refusal is terminal: no JSON fallback and no rate-limit retry.
        VerifyRequestsSent(1);
    }

    [Fact]
    public async Task TranslateBatchAsync_StreamingTemplate_IsRejectedWithoutRequest()
    {
        const string streamingTemplate =
            "{\"model\":\"{model}\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"{userMessage}\"}]}";
        UseSettings(BaseEndpoint, chatTemplate: streamingTemplate);

        var exception = await Assert.ThrowsAsync<TranslationConfigurationException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        Assert.Contains("\"stream\": true", exception.Message);
        VerifyRequestsSent(0);
    }

    [Fact]
    public async Task TranslateBatchAsync_EventStream_IsNotRetriedAsFallback()
    {
        UseSettings(BaseEndpoint);
        SetupResponse(() => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<TranslationUnusableResponseException>(
            () => _service.TranslateBatchAsync(Batch(), "en", "es", CancellationToken.None));

        VerifyRequestsSent(1);
    }

    private static HttpResponseMessage ResponsesRefusal() =>
        JsonResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "refusal", refusal = "no" } }
                }
            }
        });

    private static List<BatchSubtitleItem> Batch() =>
    [
        new() { Position = 1, Line = "Hello" },
        new() { Position = 2, Line = "World" }
    ];

    private static HttpResponseMessage ChatResponse(string translatedJson) =>
        JsonResponse(new { choices = new[] { new { message = new { content = translatedJson } } } });

    private static HttpResponseMessage ResponsesResponse(string translatedJson) =>
        JsonResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = translatedJson } }
                }
            }
        });

    private static HttpResponseMessage MessagesResponse(string text, string stopReason = "end_turn") =>
        JsonResponse(new
        {
            stop_reason = stopReason,
            content = new[] { new { type = "text", text } }
        });

    private static HttpResponseMessage MessagesToolResponse(IEnumerable<(int Position, string Line)> lines) =>
        JsonResponse(new
        {
            stop_reason = "tool_use",
            content = new[]
            {
                new
                {
                    type = "tool_use",
                    name = "record_translation_batch",
                    input = new
                    {
                        translations = lines.Select(line => new { position = line.Position, line = line.Line }).ToArray()
                    }
                }
            }
        });

    private static IEnumerable<string?> Roles(JsonElement messages) =>
        messages.EnumerateArray().Select(message => message.GetProperty("role").GetString());

    private static HttpResponseMessage JsonResponse(object body) => new()
    {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private void UseSettings(
        string endpoint,
        string model = "my-model",
        string? aiThinking = null,
        string? aiMaxTokens = null,
        string? protocol = null,
        string chatTemplate = "",
        string responsesTemplate = "",
        string messagesTemplate = "",
        string anthropicVersion = "2023-06-01")
    {
        var settings = new Dictionary<string, string>
        {
            { SettingKeys.Translation.Custom.Model, model },
            { SettingKeys.Translation.Custom.Endpoint, endpoint },
            { SettingKeys.Translation.Custom.ChatRequestTemplate, chatTemplate },
            { SettingKeys.Translation.Custom.ResponsesRequestTemplate, responsesTemplate },
            { SettingKeys.Translation.Custom.MessagesRequestTemplate, messagesTemplate },
            { SettingKeys.Translation.Custom.AnthropicVersion, anthropicVersion },
            { SettingKeys.Translation.AiPrompt, "Translate from {sourceLanguage} to {targetLanguage}" },
            { SettingKeys.Translation.AiUserPrompt, "{lineToTranslate}" },
            { SettingKeys.Translation.ProofreadPrompt, "Please proofread the line from {sourceLanguage} to {targetLanguage}" },
            { SettingKeys.Translation.ProofreadUserPrompt, "{translatedLine}" },
            { SettingKeys.Translation.RequestTimeout, "5" },
            { SettingKeys.Translation.MaxRetries, "3" },
            { SettingKeys.Translation.RetryDelay, "0" }, // No delay to keep the tests fast
            { SettingKeys.Translation.RetryDelayMultiplier, "1" },
            { SettingKeys.Translation.LanguageCodeFormat, "false" }
        };

        if (aiThinking != null)
        {
            settings[SettingKeys.Translation.AiThinking] = aiThinking;
        }

        if (aiMaxTokens != null)
        {
            settings[SettingKeys.Translation.AiMaxTokens] = aiMaxTokens;
        }

        if (protocol != null)
        {
            settings[SettingKeys.Translation.Custom.Protocol] = protocol;
        }

        _settingsMock.Setup(settingService => settingService.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(settings);
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

    private static HttpResponseMessage BadRequestResponse(string message) => new()
    {
        StatusCode = HttpStatusCode.BadRequest,
        Content = new StringContent(
            JsonSerializer.Serialize(new { error = new { message } }),
            Encoding.UTF8,
            "application/json")
    };

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

    private List<string> CaptureRequestBodiesAndRespond(Func<string, HttpResponseMessage> responseFactory)
    {
        var captured = new List<string>();
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                captured.Add(body);
                return Task.FromResult(responseFactory(body));
            });
        return captured;
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

    private void VerifyHeader(string name, string expected, Times times)
    {
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            times,
            ItExpr.Is<HttpRequestMessage>(request => HeaderEquals(request, name, expected)),
            ItExpr.IsAny<CancellationToken>());
    }

    private void VerifyHeaderAbsent(string name)
    {
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request => !request.Headers.Contains(name)),
            ItExpr.IsAny<CancellationToken>());
    }

    private static bool HeaderEquals(HttpRequestMessage request, string name, string expected)
    {
        if (!request.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value == expected)
            {
                return true;
            }
        }

        return false;
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
}
