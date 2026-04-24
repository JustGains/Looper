namespace JustCode.Services;

/// <summary>
/// Cheap whitespace-based counts for UI badges. Not a full tokenizer —
/// the word-count rule is "runs of non-whitespace characters" which is
/// good enough for a chat input badge and avoids any regex overhead on
/// every keystroke.
/// </summary>
public static class TextStats
{
    public readonly record struct Result(int Chars, int Words, int Lines)
    {
        public bool IsEmpty => Chars == 0;
    }

    public static Result Calculate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new Result(0, 0, 0);

        // Span read is a tiny win on its own but the big deal here is that
        // it lets the JIT keep the char loop in registers — calling the
        // (string, i) indexer went through a bounds-checked property each
        // iteration. Works on every keystroke for chat-input stats.
        ReadOnlySpan<char> span = text.AsSpan();
        int chars = span.Length;
        int words = 0;
        int lines = 1;
        bool inWord = false;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            // Common ASCII fast path: space/tab/newline/CR are every line's
            // whitespace; sidestep char.IsWhiteSpace for non-exotic inputs.
            bool isSpace = c <= ' '
                ? (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                : char.IsWhiteSpace(c);
            if (c == '\n') lines++;
            if (inWord)
            {
                if (isSpace) inWord = false;
            }
            else if (!isSpace)
            {
                inWord = true;
                words++;
            }
        }
        return new Result(chars, words, lines);
    }

    /// Aggregate stats across several strings. Each string's lines count as
    /// if it were a standalone document, then summed — good for "N messages,
    /// M words total" kinds of readouts where we don't care about joining
    /// them with newlines. Empty strings contribute zero to every field.
    public static Result Calculate(IEnumerable<string?>? texts)
    {
        if (texts is null) return new Result(0, 0, 0);
        int chars = 0, words = 0, lines = 0;
        foreach (var t in texts)
        {
            if (string.IsNullOrEmpty(t)) continue;
            var r = Calculate(t);
            chars += r.Chars;
            words += r.Words;
            lines += r.Lines;
        }
        return new Result(chars, words, lines);
    }

    /// Convenience: compute + format in one call. Equivalent to
    /// `FormatLabel(Calculate(text))` but saves callers the two-step dance.
    public static string FormatLabel(string? text) => FormatLabel(Calculate(text));

    public static string FormatLabel(Result r)
    {
        if (r.IsEmpty) return "";
        var wordPart = r.Words == 1
            ? $"{r.Words:N0} word"
            : $"{r.Words:N0} words";
        var tokenPart = FormatTokens(ApproxTokens(r.Chars));
        return tokenPart.Length > 0
            ? $"{wordPart} · {r.Chars:N0} chars · {tokenPart}"
            : $"{wordPart} · {r.Chars:N0} chars";
    }

    /// Rough English-prose token estimate using ≈ 3.7 chars/token. Shared
    /// with the conversation view-model's `TokenSummary` so the chat-input
    /// badge and the live token counter use the same arithmetic.
    public static long ApproxTokens(long chars) =>
        chars <= 0 ? 0 : Math.Max(1L, (long)(chars / 3.7));

    /// Canonical "~N tok" string used across badges. Returns "" for zero/negative
    /// so callers can concatenate with · separators without stray empty segments.
    public static string FormatTokens(long tokens) =>
        tokens > 0 ? $"~{tokens:N0} tok" : "";
}
