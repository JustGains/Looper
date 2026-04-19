using System.Text;
using System.Text.Json;

namespace Looper.Services;

/// Parses newline-delimited JSON from `claude --output-format stream-json
/// --include-partial-messages` into readable console chunks.
public sealed class StreamJsonFormatter
{
    private string? _activeBlockKind;
    private string? _activeToolName;
    private readonly StringBuilder _toolInputBuf = new();

    public event EventHandler<string>? SessionIdCaptured;

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
        if (!evt.TryGetProperty("content_block", out var block)) return "";
        var kind = GetString(block, "type") ?? "";
        _activeBlockKind = kind;
        switch (kind)
        {
            case "text":
                return "";
            case "thinking":
                return "\n--- thinking ---\n";
            case "tool_use":
                _activeToolName = GetString(block, "name") ?? "?";
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
                return GetString(delta, "text") ?? "";
            case "thinking_delta":
                return GetString(delta, "thinking") ?? "";
            case "input_json_delta":
                var partial = GetString(delta, "partial_json") ?? "";
                _toolInputBuf.Append(partial);
                return "";
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
        if (kind == "text" || kind == "thinking")
            return "\n";
        return "";
    }

    /// We already rendered content via stream_event deltas; skip full assistant messages.
    private static string FormatAssistant(JsonElement _) => "";

    private static string FormatUser(JsonElement root)
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
                var text = ExtractToolResultText(item);
                var prefix = isError ? "⚠" : "⎿";
                sb.Append('\n').Append(prefix).Append(' ').Append(Truncate(text, 400)).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string FormatResult(JsonElement root)
    {
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
                parts.Add(s);
            }
        }
        var detail = parts.Count > 0 ? " " + string.Join(" · ", parts) : "";
        return $"\n[done{(string.IsNullOrEmpty(subtype) ? "" : $":{subtype}")}]{detail}\n";
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

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
