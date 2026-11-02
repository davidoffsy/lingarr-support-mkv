using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services.Subtitle;

/// <summary>
/// Replaces a single ideographic full stop (U+3002 <c>。</c>) between two visible
/// characters with a space. Consecutive full stops, a trailing full stop, stops
/// inside quotes, and stops that already sit before a line break or other
/// punctuation are left unchanged.
/// </summary>
public static class InlineIdeographicFullStopReplacer
{
    public const char IdeographicFullStop = '\u3002';

    public static void Apply(IEnumerable<SubtitleItem>? subtitles)
    {
        if (subtitles is null)
        {
            return;
        }

        foreach (var item in subtitles)
        {
            Apply(item.TranslatedLines);
        }
    }

    public static void Apply(IList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return;
        }

        var visible = SubtitleVisibleText.Collect(lines);
        if (visible.Count < 3)
        {
            return;
        }

        var insideQuotes = MarkQuotes(lines, visible);
        var replacements = new List<(int VisibleIndex, bool InsertSpace)>();
        for (var index = 1; index < visible.Count - 1; index++)
        {
            if (!ShouldReplace(lines, visible, insideQuotes, index))
            {
                continue;
            }

            var insertSpace = !SubtitleVisibleText.GapSeparates(lines, visible[index], visible[index + 1]);
            replacements.Add((index, insertSpace));
        }

        for (var offset = replacements.Count - 1; offset >= 0; offset--)
        {
            var (visibleIndex, insertSpace) = replacements[offset];
            var glyph = visible[visibleIndex];
            var line = lines[glyph.Line];
            lines[glyph.Line] = insertSpace
                ? string.Concat(line.AsSpan(0, glyph.Index), " ", line.AsSpan(glyph.Index + 1))
                : line.Remove(glyph.Index, 1);
        }
    }

    private static bool ShouldReplace(
        IList<string> lines,
        IReadOnlyList<SubtitleVisibleText.Glyph> visible,
        IReadOnlyList<bool> insideQuotes,
        int index)
    {
        var glyph = visible[index];
        if (lines[glyph.Line][glyph.Index] != IdeographicFullStop)
        {
            return false;
        }

        if (insideQuotes[index])
        {
            return false;
        }

        var previous = lines[visible[index - 1].Line][visible[index - 1].Index];
        var next = lines[visible[index + 1].Line][visible[index + 1].Index];
        if (previous == IdeographicFullStop || next == IdeographicFullStop)
        {
            return false;
        }

        return !IsProtectedFollower(next);
    }

    private static bool[] MarkQuotes(IList<string> lines, IReadOnlyList<SubtitleVisibleText.Glyph> visible)
    {
        var inside = new bool[visible.Count];
        var depth = 0;
        var asciiDouble = false;
        for (var index = 0; index < visible.Count; index++)
        {
            inside[index] = depth > 0 || asciiDouble;
            var current = lines[visible[index].Line][visible[index].Index];
            switch (current)
            {
                case '「' or '『' or '“' or '‘':
                    depth++;
                    break;
                case '」' or '』' or '”' or '’':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case '"':
                    asciiDouble = !asciiDouble;
                    break;
            }
        }

        return inside;
    }

    private static bool IsProtectedFollower(char next) =>
        next is '」' or '』' or '”' or '’' or
            '？' or '！' or '，' or '、' or '；' or '：' or '…' or
            '）' or ')' or '】' or '》' or '〉' or
            '?' or '!' or ',' or ';' or ':' or '.' or
            '—' or '–';
}
