using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JustCode.Services;

/// Parses newline-delimited JSON from `claude --output-format stream-json
/// --include-partial-messages` into readable console chunks.
public sealed class StreamJsonFormatter
{
    private string? _activeBlockKind;
    private string? _activeToolName;
    private readonly StringBuilder _toolInputBuf = new();
    private readonly StringBuilder _assistantText = new();
    private readonly StringBuilder _currentBlockText = new();

    /// True while a `thinking` block is currently being streamed. Consumers
    /// (e.g. LoopRunner) can use this to relax the inactivity timeout — some
    /// models go silent for minutes while reasoning.
    public bool IsInThinking => string.Equals(_activeBlockKind, "thinking", StringComparison.Ordinal);

    // -------- Per-iteration signals the LoopRunner reads after the turn --------

    /// True if the assistant's output contains a `---RALPH_STATUS---` block
    /// with `EXIT_SIGNAL: true`.
    public bool IterationExitSignal { get; private set; }
    /// The `STATUS:` line value inside the RALPH_STATUS block (COMPLETE,
    /// IN_PROGRESS, BLOCKED, etc.), or null if no block was found.
    public string? IterationStatus { get; private set; }
    /// True if the assistant asked the user a clarifying question this turn.
    public bool IterationAskedQuestion { get; private set; }
    /// Count of `tool_result` blocks that returned `is_error: true`.
    public int IterationToolErrors { get; private set; }

    public event EventHandler<string>? SessionIdCaptured;
    public event EventHandler<(long input, long output, long cached)>? TokenUsageReported;
    /// Fires once per tool_use block opened by the assistant. Payload is the
    /// tool name (e.g. "Read", "Bash").
    public event EventHandler<string>? ToolCallInvoked;
    /// Fires on every text/thinking delta with the full accumulated block
    /// content so far. Consumers use this to pin the "last useful response"
    /// to the top of the UI while tool calls scroll past below it. Starting
    /// a new text/thinking block resets the buffer, so the pin swaps
    /// atomically as each new response/thought begins.
    public event EventHandler<(string text, bool isThinking)>? NonToolBlockUpdated;
    /// Fires with a count of characters appended to the current assistant
    /// message (text + thinking + tool_use input JSON). Consumers convert to
    /// an estimated token count (≈ chars / 3.7) until the actual `result`
    /// usage arrives and corrects it.
    public event EventHandler<int>? EstimatedOutputCharsAppended;

    private static readonly Regex RalphStatusBlock = new(
        @"---RALPH_STATUS---\s*(?<body>.*?)\s*---RALPH_STATUS---",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExitSignalLine = new(
        @"EXIT_SIGNAL\s*:\s*true",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StatusLine = new(
        @"STATUS\s*:\s*(?<v>[A-Z_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Plain-language clarifying-question patterns. Kept narrow to avoid false
    // positives on the model's own rhetorical phrasing inside prose.
    private static readonly Regex QuestionPattern = new(
        @"\b(?:should I|would you like|do you want|do you need|could you (?:confirm|clarify)|please (?:confirm|clarify)|let me know (?:which|whether|if))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Format(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = GetString(root, "type");
            return type switch
            {
                "system" => FormatSystem(root),
                "stream_event" => FormatStreamEvent(root),
                "assistant" => FormatAssistant(root),
                "user" => FormatUser(root),
                "result" => FormatResult(root),
                _ => "",
            };
        }
        catch (JsonException)
        {
            return line + "\n";
        }
    }

    private string FormatSystem(JsonElement root)
    {
        var subtype = GetString(root, "subtype");
        switch (subtype)
        {
            case "init":
            {
                var model = GetString(root, "model") ?? "?";
                var cwd = GetString(root, "cwd") ?? "?";
                var sid = GetString(root, "session_id");
                if (!string.IsNullOrEmpty(sid))
                    SessionIdCaptured?.Invoke(this, sid);
                var tail = string.IsNullOrEmpty(sid) ? "" : $" session={sid}";
                return $"\n[session] model={model} cwd={cwd}{tail}\n";
            }
            case "compact_boundary":
            case "compaction":
            case "compact":
            case "summarize":
            case "summarization":
            {
                var pre = GetNumber(root, "pre_tokens") ?? GetNumber(root, "input_tokens_before");
                var post = GetNumber(root, "post_tokens") ?? GetNumber(root, "input_tokens_after");
                var detail = (pre != null && post != null) ? $" {pre}→{post} tokens" : "";
                return $"\n[compact]{detail} — older turns summarised to stay within context\n";
            }
            default:
                return "";
        }
    }

    private static double? GetNumber(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private string FormatStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt)) return "";
        var etype = GetString(evt, "type");
        switch (etype)
        {
            case "message_start":
                return "";
            case "content_block_start":
                return OnBlockStart(evt);
            case "content_block_delta":
                return OnBlockDelta(evt);
            case "content_block_stop":
                return OnBlockStop();
            case "message_stop":
                return "";
            default:
                return "";
        }
    }

    private string OnBlockStart(JsonElement evt)
    {
        _toolInputBuf.Clear();
        _currentBlockText.Clear();
        if (!evt.TryGetProperty("content_block", out var block)) return "";
        var kind = GetString(block, "type") ?? "";
        _activeBlockKind = kind;
        switch (kind)
        {
            case "text":
                return "";
            case "thinking":
                return "\n🧠 thinking…\n│ ";
            case "redacted_thinking":
                return "\n🧠 thinking (redacted)\n│ [encrypted thinking — not shown]\n";
            case "tool_use":
                _activeToolName = GetString(block, "name") ?? "?";
                ToolCallInvoked?.Invoke(this, _activeToolName);
                return "";
            default:
                return "";
        }
    }

    private string OnBlockDelta(JsonElement evt)
    {
        if (!evt.TryGetProperty("delta", out var delta)) return "";
        var dtype = GetString(delta, "type");
        switch (dtype)
        {
            case "text_delta":
            {
                var t = GetString(delta, "text") ?? "";
                if (t.Length > 0)
                {
                    EstimatedOutputCharsAppended?.Invoke(this, t.Length);
                    _assistantText.Append(t);
                    _currentBlockText.Append(t);
                    if (!IterationAskedQuestion && QuestionPattern.IsMatch(t))
                        IterationAskedQuestion = true;
                    if (_activeBlockKind == "text")
                        NonToolBlockUpdated?.Invoke(this, (_currentBlockText.ToString(), false));
                }
                return t;
            }
            case "thinking_delta":
            {
                var th = GetString(delta, "thinking") ?? "";
                if (th.Length > 0)
                {
                    EstimatedOutputCharsAppended?.Invoke(this, th.Length);
                    _currentBlockText.Append(th);
                    if (_activeBlockKind == "thinking")
                        NonToolBlockUpdated?.Invoke(this, (_currentBlockText.ToString(), true));
                }
                // Preserve the `│ ` gutter on every new line inside the
                // thinking block so the styling rule can match them.
                return th.Replace("\n", "\n│ ");
            }
            case "input_json_delta":
            {
                var partial = GetString(delta, "partial_json") ?? "";
                _toolInputBuf.Append(partial);
                if (partial.Length > 0) EstimatedOutputCharsAppended?.Invoke(this, partial.Length);
                return "";
            }
            default:
                return "";
        }
    }

    private string OnBlockStop()
    {
        var kind = _activeBlockKind;
        _activeBlockKind = null;
        if (kind == "tool_use")
        {
            var input = _toolInputBuf.ToString();
            _toolInputBuf.Clear();
            var name = _activeToolName ?? "?";
            _activeToolName = null;
            return $"\n▸ {name}({SummariseToolInput(input)})\n";
        }
        if (kind == "thinking")
            return "\n";
        if (kind == "text")
            return "\n";
        return "";
    }

    /// We already rendered content via stream_event deltas; skip full assistant messages.
    private static string FormatAssistant(JsonElement _) => "";

    private string FormatUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return "";
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            var itype = GetString(item, "type");
            if (itype == "tool_result")
            {
                var isError = item.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                if (isError) IterationToolErrors++;
                var text = ExtractToolResultText(item);
                var prefix = isError ? "⚠" : "⎿";
                sb.Append('\n').Append(prefix).Append(' ').Append(Truncate(text, 400)).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// Default context window size assumed when computing fill % for Claude
    /// models. All current Claude 4.x tiers share a 200k window; the 1M beta
    /// variant is rare enough that we stick with 200k as a universal baseline.
    private const long DefaultContextTokens = 200_000;

    private string FormatResult(JsonElement root)
    {
        // Scan the accumulated assistant text one last time for the RALPH_STATUS
        // block. Doing it here (instead of on every delta) keeps the hot path
        // cheap and guarantees we see the whole block even if it streamed in
        // pieces.
        ParseRalphStatusFromAccumulated();

        var subtype = GetString(root, "subtype") ?? "";
        var parts = new List<string>();
        if (root.TryGetProperty("duration_ms", out var dur) && dur.ValueKind == JsonValueKind.Number)
            parts.Add($"{dur.GetDouble() / 1000.0:0.0}s");
        if (root.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number)
            parts.Add($"{turns.GetInt32()} turns");
        if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
            parts.Add($"${cost.GetDouble():0.0000}");
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var inTok = GetNumber(usage, "input_tokens");
            var outTok = GetNumber(usage, "output_tokens");
            var cached = GetNumber(usage, "cache_read_input_tokens");
            if (inTok != null || outTok != null)
            {
                var s = $"{(inTok ?? 0):N0} in / {(outTok ?? 0):N0} out";
                if (cached != null && cached > 0) s += $" ({cached:N0} cached)";
                if (inTok is > 0)
                {
                    double pct = inTok.Value * 100.0 / DefaultContextTokens;
                    s += $" · ~{pct:0.0}% ctx";
                }
                parts.Add(s);
                TokenUsageReported?.Invoke(this,
                    ((long)(inTok ?? 0), (long)(outTok ?? 0), (long)(cached ?? 0)));
            }
        }
        var detail = parts.Count > 0 ? " " + string.Join(" · ", parts) : "";
        var suffix = string.IsNullOrEmpty(subtype) || subtype == "success" ? "" : $":{subtype}";
        return $"\n[usage{suffix}]{detail}\n";
    }

    private static string SummariseToolInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Truncate(json, 120);
            var pairs = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => $"\"{Truncate(prop.Value.GetString() ?? "", 60)}\"",
                    JsonValueKind.Number => prop.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "null",
                    _ => Truncate(prop.Value.GetRawText(), 60),
                };
                pairs.Add($"{prop.Name}={v}");
            }
            return string.Join(", ", pairs);
        }
        catch
        {
            return Truncate(json, 120);
        }
    }

    private static string ExtractToolResultText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var b in c.EnumerateArray())
            {
                if (GetString(b, "type") == "text")
                    sb.Append(GetString(b, "text"));
            }
            return sb.ToString();
        }
        return "";
    }

    private void ParseRalphStatusFromAccumulated()
    {
        if (_assistantText.Length == 0) return;
        var text = _assistantText.ToString();
        var m = RalphStatusBlock.Match(text);
        if (!m.Success) return;
        var body = m.Groups["body"].Value;
        if (ExitSignalLine.IsMatch(body)) IterationExitSignal = true;
        var sm = StatusLine.Match(body);
        if (sm.Success) IterationStatus = sm.Groups["v"].Value.ToUpperInvariant();
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
