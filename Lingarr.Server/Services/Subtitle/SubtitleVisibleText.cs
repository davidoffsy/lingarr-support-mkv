namespace Lingarr.Server.Services.Subtitle;

/// <summary>
/// Walks subtitle lines while skipping ASS/HTML markup and ASS whitespace escapes
/// so punctuation helpers see the same visible characters a player would.
/// </summary>
internal static class SubtitleVisibleText
{
    public readonly record struct Glyph(int Line, int Index);

    public static List<Glyph> Collect(IList<string>? lines)
    {
        var visible = new List<Glyph>();
        if (lines is null)
        {
            return visible;
        }

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            CollectLine(lines[lineIndex], lineIndex, visible);
        }

        return visible;
    }

    /// <summary>
    /// True when the span between two visible glyphs already separates sentences:
    /// a different line, ASS <c>\N</c>/<c>\n</c>/<c>\h</c>/<c>\t</c>, or whitespace.
    /// Markup-only gaps are not a separator.
    /// </summary>
    public static bool GapSeparates(IList<string> lines, Glyph left, Glyph right)
    {
        if (left.Line != right.Line)
        {
            return true;
        }

        var line = lines[left.Line];
        var index = left.Index + 1;
        while (index < right.Index)
        {
            if (TrySkipDelimited(line, ref index, '{', '}') ||
                TrySkipDelimited(line, ref index, '<', '>'))
            {
                continue;
            }

            var current = line[index];
            if (current == '\\' && index + 1 < right.Index && IsAssWhitespaceEscape(line[index + 1]))
            {
                return true;
            }

            if (char.IsWhiteSpace(current))
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static void CollectLine(string? line, int lineIndex, List<Glyph> visible)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var index = 0;
        while (index < line.Length)
        {
            var current = line[index];
            if (TrySkipDelimited(line, ref index, '{', '}') ||
                TrySkipDelimited(line, ref index, '<', '>'))
            {
                continue;
            }

            if (current == '\\' && index + 1 < line.Length && IsAssWhitespaceEscape(line[index + 1]))
            {
                index += 2;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            visible.Add(new Glyph(lineIndex, index));
            index++;
        }
    }

    private static bool TrySkipDelimited(string line, ref int index, char open, char close)
    {
        if (line[index] != open)
        {
            return false;
        }

        var closeIndex = line.IndexOf(close, index + 1);
        if (closeIndex < 0)
        {
            return false;
        }

        index = closeIndex + 1;
        return true;
    }

    private static bool IsAssWhitespaceEscape(char next) =>
        next is 'N' or 'n' or 'h' or 't';
}
