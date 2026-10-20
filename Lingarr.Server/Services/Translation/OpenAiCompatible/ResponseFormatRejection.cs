using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lingarr.Server.Services.Translation.OpenAiCompatible;

/// <summary>
/// Recognizes explicit unsupported-format errors without treating every invalid request as
/// evidence of a provider capability limit. Request echoes and unrelated validation fields are
/// deliberately ignored; ambiguous errors keep their normal HTTP classification.
/// </summary>
internal static class ResponseFormatRejection
{
    public static bool IsUnsupported(string errorBody)
    {
        try
        {
            using var document = JsonDocument.Parse(errorBody);
            return IsUnsupportedError(document.RootElement);
        }
        catch (JsonException)
        {
            // Some gateways return a plain-text error instead of the OpenAI error envelope.
            return IsUnsupportedMessage(errorBody, formatParameter: false);
        }
    }

    private static bool IsUnsupportedError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return IsUnsupportedMessage(error.GetString()!, formatParameter: false);
        }

        if (error.ValueKind == JsonValueKind.Array)
        {
            return error.EnumerateArray().Any(IsUnsupportedError);
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (error.TryGetProperty("error", out var nestedError))
        {
            return IsUnsupportedError(nestedError);
        }

        if (error.TryGetProperty("detail", out var detail)
            && detail.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return IsUnsupportedError(detail);
        }

        var parameter = GetString(error, "param");
        if (parameter == null && error.TryGetProperty("loc", out var location)
            && location.ValueKind == JsonValueKind.Array)
        {
            parameter = string.Join(".", location.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.String)
                .Select(part => part.GetString()));
        }

        var formatParameter = IsFormatParameter(parameter);
        if (!string.IsNullOrEmpty(parameter) && !formatParameter)
        {
            return false;
        }

        var code = GetString(error, "code") ?? GetString(error, "type");
        if (formatParameter && code is "unsupported_parameter" or "unsupported_value"
                or "unknown_parameter" or "unrecognized_parameter" or "extra_forbidden")
        {
            return true;
        }

        var message = GetString(error, "message") ?? GetString(error, "msg") ?? GetString(error, "detail");
        return IsUnsupportedMessage(message, formatParameter);
    }

    private static bool IsUnsupportedMessage(string? message, bool formatParameter)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        // The format must be the rejected subject, not merely mentioned next to an unrelated
        // unsupported option or an invalid schema keyword.
        return UnsupportedFormatMessage.IsMatch(message)
            || (formatParameter && UnsupportedParameterMessage.IsMatch(message));
    }

    private static bool IsFormatParameter(string? parameter)
    {
        if (parameter?.StartsWith("body.", StringComparison.OrdinalIgnoreCase) == true)
        {
            parameter = parameter[5..];
        }

        // A nested schema property failing validation does not mean schemas are unsupported.
        return parameter?.ToLowerInvariant() is "response_format" or "response_format.type"
            or "response_format.json_schema" or "json_schema" or "json_object";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private const string QuotedFormat = @"['""`]?\b(?:response_format|json_schema|json_object)\b['""`]?";
    private const RegexOptions MatchingOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex UnsupportedFormatMessage = new(
        QuotedFormat + @"(?:\s+(?:response\s+)?(?:parameter|field|argument|type|format|mode|value))?\s+(?:is\s+)?(?:not\s+(?:supported|available|allowed|permitted)|unsupported|unavailable)\b"
        + @"|\b(?:unsupported|unknown|unrecognized|unexpected)\s+(?:(?:request\s+)?(?:parameter|field|argument|type|format|mode|value)(?:\s+supplied)?\s*:?\s*)?" + QuotedFormat
        + @"|\b(?:does\s+not|doesn't|do\s+not|cannot)\s+(?:support|accept)\s+(?:the\s+)?" + QuotedFormat,
        MatchingOptions);

    private static readonly Regex UnsupportedParameterMessage = new(
        @"^(?:this (?:parameter|field|argument|type|format|value) (?:is )?)?(?:not supported|unavailable|not available)\b",
        MatchingOptions);
}