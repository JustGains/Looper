using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JustCode.Services;

/// Parses newline-delimited JSON from `pi --mode json` into readable console
/// chunks and surfaces the same loop-control signals (session id, tool-call
/// count, pinned-response stream, RALPH_STATUS exit gate, question detection)
/// as `StreamJsonFormatter` does for Claude.
///
/// Event schema reference:
///   C:\Users\james\AppData\Roaming\npm\node_modules\@mariozechner\pi-coding-agent\docs\json.md
public sealed class PiJsonFormatter : IIterationFormatter
{
    private bool _inThinking;
    private readonly StringBuilder _assistantText = new();
    private readonly StringBuilder _currentBlockText = new();
    private string? _lastBlockKind; // "text" or "thinking" — used to detect block boundary

    public bool IsInThinking => _inThinking;

    public bool IterationExitSignal { get; private set; }
    public string? IterationStatus { get; private set; }
    public bool IterationAskedQuestion { get; private set; }
    public int IterationToolErrors { get; private set; }
    public bool IterationFatalError { get; private set; }
    public string? IterationFatalErrorMessage { get; private set; }

    public event EventHandler<string>? SessionIdCaptured;
    public event EventHandler<(long input, long output, long cached)>? TokenUsageReported;
    public event EventHandler<string>? ToolCallInvoked;
    public event EventHandler<int>? EstimatedOutputCharsAppended;
    public event EventHandler<(string text, bool isThinking)>? NonToolBlockUpdated;

    private static readonly Regex RalphStatusBlock = new(
        @"---RALPH_STATUS---\s*(?<body>.*?)\s*---RALPH_STATUS---",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExitSignalLine = new(
        @"EXIT_SIGNAL\s*:\s*true",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StatusLine = new(
        @"STATUS\s*:\s*(?<v>[A-Z_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
            var type = GetString(root, "type") ?? "";
            return type switch
            {
                "session" => HandleSession(root),
                "agent_start" => "",
                "turn_start" => "",
                "message_start" => HandleMessageStart(root),
                "message_update" => HandleMessageUpdate(root),
                "message_end" => HandleMessageEnd(root),
                "turn_end" => "",
                "agent_end" => HandleAgentEnd(root),
                "tool_execution_start" => HandleToolStart(root),
                "tool_execution_update" => "",
                "tool_execution_end" => HandleToolEnd(root),
                "queue_update" => "",
                "compaction_start" => HandleCompactionStart(root),
                "compaction_end" => HandleCompactionEnd(root),
                "auto_retry_start" => HandleRetryStart(root),
                "auto_retry_end" => "",
                _ => "",
            };
        }
        catch (JsonException)
        {
            return line + "\n";
        }
    }

    private string HandleSession(JsonElement root)
    {
        var id = GetString(root, "id");
        var cwd = GetString(root, "cwd") ?? "?";
        if (!string.IsNullOrEmpty(id))
            SessionIdCaptured?.Invoke(this, id);
        var tail = string.IsNullOrEmpty(id) ? "" : $" session={id}";
        return $"\n[session] pi cwd={cwd}{tail}\n";
    }

    private string HandleMessageUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("assistantMessageEvent", out var ev)) return "";
        var etype = GetString(ev, "type") ?? "";
        switch (etype)
        {
            case "text_delta":
            {
                var delta = GetString(ev, "delta") ?? "";
                if (delta.Length == 0) return "";
                if (_lastBlockKind != "text")
                {
                    _currentBlockText.Clear();
                    _lastBlockKind = "text";
                    _inThinking = false;
                }
                _assistantText.Append(delta);
                _currentBlockText.Append(delta);
                EstimatedOutputCharsAppended?.Invoke(this, delta.Length);
                if (!IterationAskedQuestion && QuestionPattern.IsMatch(delta))
                    IterationAskedQuestion = true;
                NonToolBlockUpdated?.Invoke(this, (_currentBlockText.ToString(), false));
                return delta;
            }
            case "thinking_delta":
            {
                var delta = GetString(ev, "delta") ?? "";
                if (delta.Length == 0) return "";
                if (_lastBlockKind != "thinking")
                {
                    _currentBlockText.Clear();
                    _lastBlockKind = "thinking";
                    _inThinking = true;
                }
                _currentBlockText.Append(delta);
                EstimatedOutputCharsAppended?.Invoke(this, delta.Length);
                NonToolBlockUpdated?.Invoke(this, (_currentBlockText.ToString(), true));
                // Mirror Claude's thinking gutter so the existing styling rule matches.
                var prefix = _lastBlockKind == "thinking" && _currentBlockText.Length == delta.Length
                    ? "\n🧠 thinking…\n│ "
                    : "";
                return prefix + delta.Replace("\n", "\n│ ");
            }
            default:
                return "";
        }
    }

    /// Surface inline model errors. Pi emits `message_start`/`message_end`
    /// with `stopReason:"error"` and `errorMessage` on the message itself
    /// when the provider rejects the request (e.g. Claude-via-GitHub-Copilot
    /// currently fails with "400 tools.0.custom.eager_input_streaming: Extra
    /// inputs are not permitted"). Before this we swallowed the whole thing
    /// and the user saw nothing — which reads as an auth/silent failure.
    private string HandleMessageStart(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return "";
        var stopReason = GetString(msg, "stopReason");
        if (!string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase)) return "";
        var err = GetString(msg, "errorMessage") ?? "(no error message)";
        var provider = GetString(msg, "provider") ?? "?";
        var model = GetString(msg, "model") ?? "?";
        IterationFatalError = true;
        IterationFatalErrorMessage = err;
        return $"\n⚠ pi error [{provider}/{model}]: {err}\n";
    }

    private string HandleMessageEnd(JsonElement root)
    {
        _inThinking = false;
        return "";
    }

    private string HandleAgentEnd(JsonElement root)
    {
        // Final RALPH_STATUS parse over everything the assistant said this turn.
        if (_assistantText.Length > 0)
        {
            var text = _assistantText.ToString();
            var m = RalphStatusBlock.Match(text);
            if (m.Success)
            {
                var body = m.Groups["body"].Value;
                if (ExitSignalLine.IsMatch(body)) IterationExitSignal = true;
                var sm = StatusLine.Match(body);
                if (sm.Success) IterationStatus = sm.Groups["v"].Value.ToUpperInvariant();
            }
        }

        var parts = new List<string>();
        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            // Try to find an aggregate usage object on the last assistant message.
            foreach (var msg in msgs.EnumerateArray())
            {
                if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                    continue;
                var inTok = GetNumber(usage, "input_tokens") ?? GetNumber(usage, "promptTokens");
                var outTok = GetNumber(usage, "output_tokens") ?? GetNumber(usage, "completionTokens");
                var cached = GetNumber(usage, "cache_read_input_tokens");
                if (inTok != null || outTok != null)
                {
                    parts.Add($"{(inTok ?? 0):N0} in / {(outTok ?? 0):N0} out");
                    TokenUsageReported?.Invoke(this, ((long)(inTok ?? 0), (long)(outTok ?? 0), (long)(cached ?? 0)));
                }
            }
        }
        var detail = parts.Count > 0 ? " " + string.Join(" · ", parts) : "";
        return $"\n[usage]{detail}\n";
    }

    private string HandleToolStart(JsonElement root)
    {
        var name = GetString(root, "toolName") ?? "?";
        ToolCallInvoked?.Invoke(this, name);
        string args = "";
        if (root.TryGetProperty("args", out var argsEl))
            args = SummariseArgs(argsEl);
        // A new tool call means we leave the current text/thinking block —
        // the next text delta will start a fresh pinned-response buffer.
        _lastBlockKind = null;
        _inThinking = false;
        return $"\n▸ {name}({args})\n";
    }

    private string HandleToolEnd(JsonElement root)
    {
        var isError = root.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
        if (isError) IterationToolErrors++;
        string result = "";
        if (root.TryGetProperty("result", out var res))
            result = ExtractResultText(res);
        var prefix = isError ? "⚠" : "⎿";
        return $"{prefix} {Truncate(result, 400)}\n";
    }

    private string HandleRetryStart(JsonElement root)
    {
        var attempt = (int?)GetNumber(root, "attempt") ?? 0;
        var maxAttempts = (int?)GetNumber(root, "maxAttempts") ?? 0;
        var err = GetString(root, "errorMessage") ?? "";
        return $"\n[retry] attempt {attempt}/{maxAttempts} — {err}\n";
    }

    /// Pi emits `compaction_start { reason: "manual" | "threshold" | "overflow" }`
    /// when it begins shrinking context. We render a banner so the user can
    /// tell this is happening, why, and that it's about to pause briefly.
    private string HandleCompactionStart(JsonElement root)
    {
        var reason = GetString(root, "reason") ?? "auto";
        var why = reason switch
        {
            "threshold" => "context window filling up",
            "overflow" => "context overflowed",
            "manual" => "/compact requested",
            _ => reason,
        };
        var sb = new StringBuilder();
        sb.Append($"\n── compaction · {reason} ──────────────────\n");
        sb.Append($"[compact] {why} — summarising older turns…\n");
        return sb.ToString();
    }

    /// Pi emits `compaction_end { reason, result: CompactionResult|undefined,
    /// aborted, willRetry, errorMessage? }`. When `result` is present it
    /// contains the new CompactionEntry (has `tokensBefore` + the summary).
    private string HandleCompactionEnd(JsonElement root)
    {
        var aborted = root.TryGetProperty("aborted", out var ab) && ab.ValueKind == JsonValueKind.True;
        var willRetry = root.TryGetProperty("willRetry", out var wr) && wr.ValueKind == JsonValueKind.True;
        var err = GetString(root, "errorMessage");
        var sb = new StringBuilder();
        if (aborted)
        {
            var suffix = willRetry ? " — will retry" : "";
            sb.Append("[compact] ⚠ aborted");
            if (!string.IsNullOrEmpty(err)) sb.Append(": ").Append(err);
            sb.Append(suffix).Append('\n');
        }
        else
        {
            double? tokensBefore = null;
            if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
            {
                tokensBefore = GetNumber(res, "tokensBefore");
            }
            if (tokensBefore is > 0)
                sb.Append($"[compact] ✓ done — ~{tokensBefore.Value:N0} tokens reclaimed\n");
            else
                sb.Append("[compact] ✓ done — context compacted\n");
        }
        sb.Append("────────────────────────────────────────────\n");
        return sb.ToString();
    }

    private static string SummariseArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return Truncate(args.GetRawText(), 120);
        var pairs = new List<string>();
        foreach (var prop in args.EnumerateObject())
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

    private static string ExtractResultText(JsonElement res)
    {
        return res.ValueKind switch
        {
            JsonValueKind.String => res.GetString() ?? "",
            JsonValueKind.Object => Truncate(res.GetRawText(), 400),
            JsonValueKind.Array => Truncate(res.GetRawText(), 400),
            _ => res.ToString(),
        };
    }

    private static double? GetNumber(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
