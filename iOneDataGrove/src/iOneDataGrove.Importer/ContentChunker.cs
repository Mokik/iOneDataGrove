namespace iOneDataGrove.Importer;

internal static class ContentChunker
{
    internal const int DefaultMaxTextCharacters = 4_000;
    internal const int DefaultTextOverlapCharacters = 400;
    internal const int DefaultMaxFileLines = 120;
    internal const int DefaultFileOverlapLines = 20;
    internal const int DefaultMaxFileCharacters = 8_000;

    internal static IReadOnlyList<GeneratedChunk> SplitText(
        string? content,
        int maxCharacters = DefaultMaxTextCharacters,
        int overlapCharacters = DefaultTextOverlapCharacters)
    {
        ValidateTextOptions(maxCharacters, overlapCharacters);
        var normalized = Normalize(content).Trim();
        if (normalized.Length == 0) return [];

        var chunks = new List<GeneratedChunk>();
        var start = 0;
        while (start < normalized.Length)
        {
            var end = Math.Min(start + maxCharacters, normalized.Length);
            if (end < normalized.Length)
            {
                var minimumBreak = start + (maxCharacters * 3 / 5);
                var breakAt = FindBreak(normalized, minimumBreak, end);
                if (breakAt > start) end = breakAt;
            }

            var text = normalized[start..end].Trim();
            if (text.Length > 0)
            {
                chunks.Add(new GeneratedChunk(text, null, null));
            }

            if (end >= normalized.Length) break;
            var nextStart = Math.Max(start + 1, end - overlapCharacters);
            while (nextStart < end && char.IsWhiteSpace(normalized[nextStart])) nextStart++;
            start = nextStart;
        }

        return chunks;
    }

    internal static IReadOnlyList<GeneratedChunk> SplitLines(
        string? content,
        int baseStartLine = 1,
        int maxLines = DefaultMaxFileLines,
        int overlapLines = DefaultFileOverlapLines,
        int maxCharacters = DefaultMaxFileCharacters)
    {
        if (baseStartLine < 1) throw new ArgumentOutOfRangeException(nameof(baseStartLine));
        if (maxLines < 1) throw new ArgumentOutOfRangeException(nameof(maxLines));
        if (overlapLines < 0 || overlapLines >= maxLines)
            throw new ArgumentOutOfRangeException(nameof(overlapLines));
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        var normalized = Normalize(content);
        if (string.IsNullOrWhiteSpace(normalized)) return [];

        var lines = normalized.Split('\n');
        var chunks = new List<GeneratedChunk>();
        var start = 0;

        while (start < lines.Length)
        {
            var end = Math.Min(start + maxLines, lines.Length);
            while (end > start + 1 && JoinedLength(lines, start, end) > maxCharacters)
            {
                end--;
            }

            if (end == start + 1 && lines[start].Length > maxCharacters)
            {
                foreach (var part in SplitText(lines[start], maxCharacters, Math.Min(400, maxCharacters / 10)))
                {
                    chunks.Add(new GeneratedChunk(
                        part.Content,
                        baseStartLine + start,
                        baseStartLine + start));
                }
            }
            else
            {
                var text = string.Join('\n', lines[start..end]).Trim();
                if (text.Length > 0)
                {
                    chunks.Add(new GeneratedChunk(
                        text,
                        baseStartLine + start,
                        baseStartLine + end - 1));
                }
            }

            if (end >= lines.Length) break;
            start = Math.Max(start + 1, end - overlapLines);
        }

        return chunks;
    }

    internal static string ExtractLines(string content, int startLine, int endLine)
    {
        if (startLine < 1) throw new ArgumentOutOfRangeException(nameof(startLine));
        if (endLine < startLine) throw new ArgumentOutOfRangeException(nameof(endLine));

        var lines = Normalize(content).Split('\n');
        if (startLine > lines.Length) return string.Empty;
        var end = Math.Min(endLine, lines.Length);
        return string.Join('\n', lines[(startLine - 1)..end]);
    }

    private static void ValidateTextOptions(int maxCharacters, int overlapCharacters)
    {
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (overlapCharacters < 0 || overlapCharacters >= maxCharacters)
            throw new ArgumentOutOfRangeException(nameof(overlapCharacters));
    }

    private static int FindBreak(string content, int minimum, int maximum)
    {
        for (var index = maximum; index >= minimum; index--)
        {
            if (index >= content.Length) continue;
            if (content[index] == '\n') return index;
        }

        for (var index = maximum; index >= minimum; index--)
        {
            if (index >= content.Length) continue;
            if (char.IsWhiteSpace(content[index])) return index;
        }

        return maximum;
    }

    private static int JoinedLength(string[] lines, int start, int end)
    {
        var length = Math.Max(0, end - start - 1);
        for (var index = start; index < end; index++) length += lines[index].Length;
        return length;
    }

    private static string Normalize(string? content) =>
        (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}

internal sealed record GeneratedChunk(string Content, int? StartLine, int? EndLine);
