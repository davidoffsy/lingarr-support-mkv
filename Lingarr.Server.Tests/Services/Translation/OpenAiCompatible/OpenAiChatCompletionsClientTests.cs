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
using Lingarr.Core.Configuration;
using Lingarr.Server.Services.Translation;
using Lingarr.Server.Services.Translation.OpenAiCompatible;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation.OpenAiCompatible;

public class OpenAiChatCompletionsClientTests
{
    private const string Endpoint = "http://localhost:8000/v1/chat/completions";
    private const string ValidJson =
        "[{\"position\":1,\"line\":\"Hola\"},{\"position\":2,\"line\":\"Mundo\"}]";
    private const string WrapperJson = "{\"translations\":" + ValidJson + "}";
    private const string UnsupportedFormat =
        "{\"error\":{\"message\":\"response_format of type json_schema is not supported with this model.\"}}";
    private const string ContextLengthError =
        "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"Maximum context length exceeded.\",\"param\":\"messages\"}}";

    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock = new();
    private readonly OpenAiChatCompletionsClient _client;
    private readonly string _defaultTemplate;
    private readonly Dictionary<string, string> _replacements = new()
    {
        ["model"] = "my-model",
        ["systemPrompt"] = "Translate this",
        ["userMessage"] = "Hello"
    };

    public OpenAiChatCompletionsClientTests()
    {
        var templateService = new RequestTemplateService();
        _defaultTemplate = templateService.GetDefaultTemplate(SettingKeys.Translation.Custom.ChatRequestTemplate)!;
        _client = new OpenAiChatCompletionsClient(
            new HttpClient(_httpMessageHandlerMock.Object), templateService, Mock.Of<ILogger>(), "Custom");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, UnsupportedFormat)]
    [InlineData(HttpStatusCode.UnprocessableEntity, UnsupportedFormat)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"unsupported_parameter\",\"param\":\"response_format\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"unsupported_value\",\"param\":\"response_format.type\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"This response_format type is unavailable now\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "Unknown parameter: 'response_format'")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"This model does not support json_schema\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unrecognized request argument supplied: response_format\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"param\":\"response_format\",\"message\":\"Not supported with this model\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"detail\":[{\"type\":\"extra_forbidden\",\"loc\":[\"body\",\"response_format\"],\"msg\":\"Extra inputs are not permitted\"}]}")]
    public async Task CompleteBatchAsync_ExplicitFormatRejections_UseObjectPromptAndWrapper(
        HttpStatusCode status, string errorBody)
    {
        var bodies = CaptureRequests(body => GetFormat(body) switch
        {
            "json_schema" => HttpError(status, errorBody),
            "json_object" => ChatOk(WrapperJson),
            _ => ChatOk(ValidJson)
        });

        for (var i = 0; i < 4; i++)
        {
            var result = await CompleteBatchAsync();
            Assert.Equal("Hola", result[1]);
            Assert.Equal("Mundo", result[2]);
        }

        Assert.Equal(new string?[]
        {
            "json_schema", null, "json_schema", null, "json_schema", null, "json_object"
        }, bodies.Select(GetFormat));
        using var lastRequest = JsonDocument.Parse(bodies[^1]);
        var prompt = lastRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("Return only a JSON object", prompt);
        Assert.Contains("Example: {\"translations\":[{\"position\": 1, \"line\": \"translated text\"}]}", prompt);
        Assert.DoesNotContain("Return only a JSON array", prompt);
        Assert.Single(lastRequest.RootElement.GetProperty("response_format").EnumerateObject());
        Assert.Equal("Translate this", _replacements["systemPrompt"]);
    }

    [Theory]
    [InlineData(ValidJson)]
    [InlineData("{\"other\":" + ValidJson + "}")]
    public async Task CompleteBatchAsync_JsonObjectWithoutTranslationsWrapper_UsesPlainFallbackWithoutDowngrading(
        string malformedWrapper)
    {
        var objectAttempts = 0;
        var bodies = CaptureRequests(body => GetFormat(body) switch
        {
            "json_schema" => HttpError(HttpStatusCode.BadRequest, UnsupportedFormat),
            "json_object" => ChatOk(++objectAttempts == 1 ? malformedWrapper : WrapperJson),
            _ => ChatOk(ValidJson)
        });
        await MoveToJsonObjectAsync();

        var result = await CompleteBatchAsync();
        var nextResult = await CompleteBatchAsync();

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", nextResult[2]);
        // A bare or wrongly wrapped array must not be accepted as a json_object success.
        // Parsing failures still use the legacy fallback but never lower the provider capability.
        Assert.Equal(9, bodies.Count);
        Assert.Equal(new string?[] { "json_object", null, "json_object" }, bodies.Skip(6).Select(GetFormat));
        using var plainRequest = JsonDocument.Parse(bodies[7]);
        var plainPrompt = plainRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("Return only a JSON array", plainPrompt);
        Assert.DoesNotContain("Return only a JSON object", plainPrompt);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ContextLengthError)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ContextLengthError)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"context_length_exceeded\",\"param\":null,\"message\":\"Maximum context length exceeded.\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"unsupported_parameter\",\"param\":\"temperature\",\"message\":\"Unsupported temperature when using response_format json_schema.\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported parameter: temperature when using response_format json_schema.\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"invalid_json_schema\",\"param\":\"response_format\",\"message\":\"Invalid schema for response_format: required must include all keys.\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"param\":\"response_format\",\"message\":\"Schema for response_format uses unsupported keyword: pattern.\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"param\":\"response_format.json_schema.schema.pattern\",\"code\":\"unsupported_value\",\"message\":\"Unsupported schema keyword.\"}}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"detail\":[{\"type\":\"extra_forbidden\",\"loc\":[\"body\",\"temperature\"],\"msg\":\"Extra inputs are not permitted\"}],\"request\":{\"response_format\":\"json_schema\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"message\":\"Request is too large\",\"request\":{\"response_format\":\"json_schema\",\"note\":\"unsupported\"}}")]
    [InlineData(HttpStatusCode.BadRequest, "Bad Request")]
    public async Task CompleteBatchAsync_NonFormatErrorWithSuccessfulFallback_DoesNotDowngrade(
        HttpStatusCode status, string errorBody)
    {
        var bodies = CaptureRequests(body => GetFormat(body) != null
            ? HttpError(status, errorBody)
            : ChatOk(ValidJson));

        for (var i = 0; i < 4; i++)
        {
            var result = await CompleteBatchAsync();
            Assert.Equal("Hola", result[1]);
        }

        Assert.Equal(new string?[]
        {
            "json_schema", null, "json_schema", null, "json_schema", null, "json_schema", null
        }, bodies.Select(GetFormat));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task CompleteBatchAsync_NonFormatErrorOnBothAttempts_PreservesHttpFailure(
        HttpStatusCode status)
    {
        var bodies = CaptureRequests(_ => HttpError(status, ContextLengthError));

        var exception = await Assert.ThrowsAsync<TranslationException>(CompleteBatchAsync);

        Assert.Contains("context_length_exceeded", exception.Message);
        Assert.Equal(new string?[] { "json_schema", null }, bodies.Select(GetFormat));
    }

    [Fact]
    public async Task CompleteBatchAsync_JsonObjectContextLengthError_DoesNotDisableResponseFormat()
    {
        var bodies = CaptureRequests(body => GetFormat(body) switch
        {
            "json_schema" => HttpError(HttpStatusCode.BadRequest, UnsupportedFormat),
            "json_object" => HttpError(HttpStatusCode.BadRequest, ContextLengthError),
            _ => ChatOk(ValidJson)
        });
        await MoveToJsonObjectAsync();

        for (var i = 0; i < 4; i++)
        {
            var result = await CompleteBatchAsync();
            Assert.Equal("Hola", result[1]);
        }

        Assert.Equal(new string?[]
        {
            "json_object", null, "json_object", null, "json_object", null, "json_object", null
        }, bodies.Skip(6).Select(GetFormat));
    }

    [Fact]
    public async Task CompleteBatchAsync_NumberedList_DoesNotSendFallbackRequest()
    {
        var bodies = CaptureRequests(_ => ChatOk("1. Hola\n2. Mundo"));

        var result = await CompleteBatchAsync();

        Assert.Equal("Hola", result[1]);
        Assert.Equal("Mundo", result[2]);
        Assert.Single(bodies);
        Assert.Equal("json_schema", GetFormat(bodies[0]));
    }

    [Fact]
    public async Task CompleteBatchAsync_FormatProbe_LogsFirstRejectionAsWarningNotError()
    {
        var logger = new RecordingLogger();
        var client = new OpenAiChatCompletionsClient(
            new HttpClient(_httpMessageHandlerMock.Object),
            new RequestTemplateService(),
            logger,
            "Custom");
        CaptureRequests(body => GetFormat(body) == "json_schema"
            ? HttpError(HttpStatusCode.BadRequest, UnsupportedFormat)
            : ChatOk(ValidJson));

        for (var i = 0; i < 2; i++)
        {
            await client.CompleteBatchAsync(
                Endpoint, null, _defaultTemplate, _replacements, AiThinkingMode.Auto, 0,
                CancellationToken.None);
        }

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("json_schema"));
    }

    private async Task MoveToJsonObjectAsync()
    {
        for (var i = 0; i < BatchResponseFormatPolicy.JsonSchemaRejectionLimit; i++)
        {
            await CompleteBatchAsync();
        }
    }

    private Task<Dictionary<int, string>> CompleteBatchAsync() =>
        _client.CompleteBatchAsync(Endpoint, null, _defaultTemplate, _replacements, AiThinkingMode.Auto, 0,
            CancellationToken.None);

    private static string? GetFormat(JsonElement body) =>
        body.TryGetProperty("response_format", out var format) ? format.GetProperty("type").GetString() : null;

    private static string? GetFormat(string body)
    {
        using var document = JsonDocument.Parse(body);
        return GetFormat(document.RootElement);
    }

    private static HttpResponseMessage ChatOk(string text) => new()
    {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent(
            JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }),
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage HttpError(HttpStatusCode status, string errorBody) => new()
    {
        StatusCode = status,
        Content = new StringContent(errorBody, Encoding.UTF8, "application/json")
    };

    private List<string> CaptureRequests(Func<JsonElement, HttpResponseMessage> responseFactory)
    {
        var captured = new List<string>();
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, token) =>
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                captured.Add(body);
                using var document = JsonDocument.Parse(body);
                return responseFactory(document.RootElement);
            });
        return captured;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}