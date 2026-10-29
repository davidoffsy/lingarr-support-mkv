using System.Text.Json;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Services.Translation.Batch;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Services.Translation.OpenAiCompatible;

/// <summary>
/// Parses batch translation payloads the model returned as text. The structured wrapper
/// (<c>{"translations":[{"position":1,"line":"..."}]}</c>) is preferred; the plain-JSON fallback
/// extracts the first array from a chatty answer, because not every gateway enforces structured
/// output. Models that ignore JSON entirely and answer <c>1. translated line</c> are recovered
/// from the same response. Unparsable text raises <see cref="TranslationParseException"/> so the
/// caller can retry.
/// </summary>
public static class BatchJsonParser
{
    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads the structured wrapper object. Shared by the Chat Completions and Responses adapters.
    /// </summary>
    public static Dictionary<int, string> ParseStructuredWrapper(string modelText, ILogger logger)
    {
        var text = StripMarkdownFence(modelText);
        try
        {
            var wrapper = JsonSerializer.Deserialize<JsonElement>(text);
            if (wrapper.ValueKind != JsonValueKind.Object
                || !wrapper.TryGetProperty("translations", out var translationsElement))
            {
                throw new TranslationParseException("Response does not contain 'translations' property");
            }

            var translatedItems =
                JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translationsElement.GetRawText());
            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles");
            }

            return BatchResponseMerger.MergeByPosition(translatedItems);
        }
        catch (Exception ex) when (ex is JsonException or TranslationParseException)
        {
            if (TryMergeNumberedList(text, logger, out var numbered))
            {
                return numbered;
            }

            logger.LogError(ex, "Failed to parse structured JSON response: {Json}", modelText);
            if (ex is TranslationParseException)
            {
                throw;
            }

            throw new TranslationParseException("Failed to parse structured translated subtitles", ex);
        }
    }

    /// <summary>
    /// Extracts the first JSON array from <paramref name="modelText"/> and reads it as batch items.
    /// Falls back to a numbered list when the model ignored the JSON instruction.
    /// </summary>
    public static Dictionary<int, string> ParseJsonArray(string modelText, ILogger logger)
    {
        var text = StripMarkdownFence(modelText);
        try
        {
            var translatedItems = JsonSerializer.Deserialize<List<StructuredBatchResponse>>(
                ExtractJsonArray(text),
                CaseInsensitive);

            if (translatedItems != null)
            {
                return BatchResponseMerger.MergeByPosition(translatedItems);
            }
        }
        catch (JsonException)
        {
            // The model often wraps translations in commentary, or ignores JSON entirely.
            // A numbered list in the same payload is still usable.
        }

        if (TryMergeNumberedList(text, logger, out var numbered))
        {
            return numbered;
        }

        logger.LogError("Failed to parse JSON response: {Json}", modelText);
        throw new TranslationParseException("Failed to parse JSON translated subtitles");
    }

    private static bool TryMergeNumberedList(
        string modelText,
        ILogger logger,
        out Dictionary<int, string> result)
    {
        if (!BatchNumberedListParser.TryParse(modelText, out var items))
        {
            result = null!;
            return false;
        }

        logger.LogWarning(
            "Batch response was a numbered list instead of JSON; parsed {Count} translations.",
            items.Count);
        result = BatchResponseMerger.MergeByPosition(items);
        return true;
    }

    /// <summary>
    /// Drops a leading markdown fence (<c>```json</c> / <c>```</c>) so a chatty model that wrapped
    /// the payload still deserializes.
    /// </summary>
    private static string StripMarkdownFence(string modelText)
    {
        var text = modelText.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        text = text[(firstNewline + 1)..];
        var fence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            text = text[..fence];
        }

        return text.Trim();
    }

    private static string ExtractJsonArray(string modelText)
    {
        var jsonStart = modelText.IndexOf('[');
        var jsonEnd = modelText.LastIndexOf(']');
        return jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart
            ? modelText.Substring(jsonStart, jsonEnd - jsonStart + 1)
            : modelText;
    }
}
