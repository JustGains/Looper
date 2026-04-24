namespace JustCode.Services;

/// <summary>
/// Shared classification for a single rendered console line.
/// The split-console, tool-call collapsing, and per-tab counting paths
/// all want the same yes/no answers about the same line — centralising
/// them here keeps the detection logic in one place and lets each caller
/// pay the trim + inspection cost once per line instead of two or three
/// times.
/// </summary>
/// <remarks>
/// Previously used compiled regexes (<c>^▸\s.+?\(</c> / <c>^⎿\s</c>);
/// console output is high-volume and these run on every line, so the
/// regex engine was disproportionate for what is really a literal-prefix
/// check. Now uses <see cref="string.StartsWith(string)"/> plus a cheap
/// <see cref="string.IndexOf(char)"/>, which is allocation-free and hot.
/// </remarks>
public static class ConsoleLineClassifier
{
    // Header lines look like:  ▸ Tool(…)
    // Result lines look like:  ⎿  <text>
    // The U+25B8 / U+23BF glyphs double as visual gutters in the rendered
    // console, so a literal prefix check is both sufficient and precise.
    private const string ToolHeaderPrefix = "▸ ";  // "▸ "
    private const string ToolResultPrefix = "⎿ ";  // "⎿ "

    public readonly record struct Classification(
        string Trimmed,
        bool IsToolHeader,
        bool IsToolResult,
        bool IsCounted)
    {
        public bool IsTool => IsToolHeader || IsToolResult;
    }

    public static Classification Classify(string line)
    {
        var trimmed = TrimTrailingNewline(line);
        bool isHeader = IsToolHeader(trimmed);
        bool isResult = !isHeader && trimmed.StartsWith(ToolResultPrefix);
        bool isCounted = !string.IsNullOrWhiteSpace(line);
        return new Classification(trimmed, isHeader, isResult, isCounted);
    }

    private static string TrimTrailingNewline(string line)
    {
        if (line.Length == 0) return line;
        char last = line[^1];
        if (last != '\n' && last != '\r') return line;
        int end = line.Length - 1;
        if (end > 0 && last == '\n' && line[end - 1] == '\r') end--;
        return line.Substring(0, end);
    }

    private static bool IsToolHeader(string trimmed)
    {
        // "▸ Name("  — require the prefix, at least one character of
        // tool name, then an opening paren so plain "▸ " prose lines
        // don't get swallowed as tool headers.
        if (!trimmed.StartsWith(ToolHeaderPrefix)) return false;
        int paren = trimmed.IndexOf('(', ToolHeaderPrefix.Length);
        return paren > ToolHeaderPrefix.Length;
    }
}
