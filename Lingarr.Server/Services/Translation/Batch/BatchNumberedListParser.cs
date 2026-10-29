using System.Globalization;
using System.Text.RegularExpressions;
using Lingarr.Contracts.Models.Batch;

namespace Lingarr.Server.Services.Translation.Batch;

/// <summary>
/// Reads batch translations the model wrote as a numbered list instead of JSON.
/// Custom and local models often ignore <c>response_format</c> and answer
/// <c>1. translated line</c>, using the subtitle position as the number.
/// </summary>
public static class BatchNumberedListParser
{
    /// <summary>
    /// A list item at the start of a line: <c>1. text</c>, <c>1、text</c>, <c>1) text</c>.
    /// The separator must not be a decimal (so <c>1.5</c> is not an item).
    /// </summary>
    private static readonly Regex LineItem = new(
        @"^\s*(\d+)(?:[.．)）:：]\s+|、\s*)(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a single line that packed several items together, which is what some
    /// gateways (and log formatters) emit instead of one item per line.
    /// </summary>
    private static readonly Regex InlineItem = new(
        @"(?:^|\s)(\d+)(?:[.．)）:：]\s+|、\s*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Tries to read <paramref name="modelText"/> as a numbered list. Returns
    /// <c>false</c> when no item could be found, so the caller can keep the original
    /// parse error.
    /// </summary>
    public static bool TryParse(string modelText, out List<StructuredBatchResponse> items)
    {
        items = [];
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return false;
        }

        var parsed = new List<StructuredBatchResponse>();
        StructuredBatchResponse? current = null;

        foreach (var rawLine in modelText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var inline = SplitInline(rawLine);
            if (inline is { Count: > 1 })
            {
                Flush(parsed, ref current);
                parsed.AddRange(inline);
                continue;
            }

            var match = LineItem.Match(rawLine);
            if (match.Success && TryReadPosition(match.Groups[1].Value, out var position))
            {
                Flush(parsed, ref current);
                current = new StructuredBatchResponse
                {
                    Position = position,
                    Line = match.Groups[2].Value.TrimEnd()
                };
                continue;
            }

            if (current == null || string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            current.Line = string.IsNullOrEmpty(current.Line)
                ? rawLine.Trim()
                : current.Line + "\n" + rawLine.Trim();
        }

        Flush(parsed, ref current);
        if (parsed.Count == 0)
        {
            return false;
        }

        items = parsed;
        return true;
    }

    private static void Flush(List<StructuredBatchResponse> parsed, ref StructuredBatchResponse? current)
    {
        if (current == null)
        {
            return;
        }

        parsed.Add(current);
        current = null;
    }

    private static List<StructuredBatchResponse>? SplitInline(string line)
    {
        var matches = InlineItem.Matches(line);
        if (matches.Count < 2)
        {
            return null;
        }

        var leading = line[..matches[0].Index];
        if (!string.IsNullOrWhiteSpace(leading))
        {
            return null;
        }

        var items = new List<StructuredBatchResponse>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!TryReadPosition(match.Groups[1].Value, out var position))
            {
                return null;
            }

            var valueStart = match.Index + match.Length;
            var valueEnd = index + 1 < matches.Count ? matches[index + 1].Index : line.Length;
            items.Add(new StructuredBatchResponse
            {
                Position = position,
                Line = line[valueStart..valueEnd].Trim()
            });
        }

        return items;
    }

    private static bool TryReadPosition(string value, out int position) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out position) &&
        position > 0;
}
