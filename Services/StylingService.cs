using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace JustCode.Services;

public sealed class StylingRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string? Replacement { get; set; }
    public string? Foreground { get; set; }
    public string? Background { get; set; }
    public string? FontWeight { get; set; }
    public string? FontStyle { get; set; }
    public bool Underline { get; set; }

    [JsonIgnore] public Regex? CompiledRegex { get; private set; }
    [JsonIgnore] public Brush? ForegroundBrush { get; private set; }
    [JsonIgnore] public Brush? BackgroundBrush { get; private set; }
    [JsonIgnore] public FontWeight? WeightValue { get; private set; }
    [JsonIgnore] public FontStyle? StyleValue { get; private set; }

    public void Compile()
    {
        if (!string.IsNullOrEmpty(Pattern))
        {
            try { CompiledRegex = new Regex(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
            catch { CompiledRegex = null; }
        }
        ForegroundBrush = ParseBrush(Foreground);
        BackgroundBrush = ParseBrush(Background);
        WeightValue = FontWeight?.ToLowerInvariant() switch
        {
            "bold" => FontWeights.Bold,
            "semibold" => FontWeights.SemiBold,
            "light" => FontWeights.Light,
            "normal" => FontWeights.Normal,
            _ => null,
        };
        StyleValue = FontStyle?.ToLowerInvariant() switch
        {
            "italic" => FontStyles.Italic,
            "normal" => FontStyles.Normal,
            _ => null,
        };
    }

    private static Brush? ParseBrush(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(s);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch { return null; }
    }
}

public static class StylingDefaults
{
    public static List<StylingRule> BuildDefaults() => new()
    {
        // --- emoji / icon replacements (first so they win ties) ---
        new StylingRule { Name = "mcp android tool",
            Pattern = @"mcp__android-mcp__[A-Za-z0-9_-]+",
            Replacement = "🤖 android",
            Foreground = "#a4c639", FontWeight = "SemiBold" },
        new StylingRule { Name = "mcp generic tool",
            Pattern = @"mcp__([a-z0-9][a-z0-9-]*)__([A-Za-z0-9_-]+)",
            Replacement = "🧩 $1:$2",
            Foreground = "#b5c6e6" },

        new StylingRule { Name = "iteration separator",
            Pattern = @"^── iteration \d+/\d+ ──$",
            Foreground = "#c586c0", FontWeight = "Bold" },
        new StylingRule { Name = "justcode info",
            Pattern = @"^\[justcode\][^\r\n]*",
            Foreground = "#dcdcaa" },
        new StylingRule { Name = "claude session init",
            Pattern = @"^\[session\][^\r\n]*",
            Foreground = "#4ec9b0" },
        new StylingRule { Name = "claude done",
            Pattern = @"^\[done[^\]]*\][^\r\n]*",
            Foreground = "#6a9955", FontWeight = "SemiBold" },
        new StylingRule { Name = "tool use header",
            Pattern = @"^▸ [^\(]+\([^\r\n]*",
            Foreground = "#7dc4ff" },
        new StylingRule { Name = "tool result",
            Pattern = @"^⎿ [^\r\n]*",
            Foreground = "#9a9ab0" },
        new StylingRule { Name = "tool error",
            Pattern = @"^⚠ [^\r\n]*",
            Foreground = "#f48771", FontWeight = "SemiBold" },
        new StylingRule { Name = "thinking header",
            Pattern = @"^🧠 thinking[^\r\n]*$",
            Foreground = "#c189e8", FontStyle = "Italic", FontWeight = "SemiBold" },
        new StylingRule { Name = "thinking line",
            Pattern = @"^│ .*$",
            Foreground = "#9a9aa0", FontStyle = "Italic" },
        new StylingRule { Name = "codex timestamp line",
            Pattern = @"^\[\d{4}-\d{2}-\d{2}T[\d:.+\-Z]+\][^\r\n]*",
            Foreground = "#7c7c82" },
        new StylingRule { Name = "codex user block",
            Pattern = @"^User instructions:\s*$",
            Foreground = "#dcdcaa", FontWeight = "SemiBold" },
        new StylingRule { Name = "codex thinking",
            Pattern = @"^thinking$",
            Foreground = "#808088", FontStyle = "Italic" },
        new StylingRule { Name = "codex exec command",
            Pattern = @"^exec\s",
            Foreground = "#7dc4ff" },
        new StylingRule { Name = "codex tokens summary",
            Pattern = @"^tokens used:.*",
            Foreground = "#6a9955" },
        new StylingRule { Name = "file path (unix)",
            Pattern = @"(?:^|\s)(/[\w./\-]+)",
            Foreground = "#9cdcfe" },
        new StylingRule { Name = "file path (windows)",
            Pattern = @"[A-Za-z]:\\[\w.\\\-]+",
            Foreground = "#9cdcfe" },
        new StylingRule { Name = "url",
            Pattern = @"https?://[^\s)]+",
            Foreground = "#569cd6", Underline = true },
        new StylingRule { Name = "error keyword",
            Pattern = @"\b(?:error|Error|ERROR|failed|Failed|FAILED|panic|Exception)\b",
            Foreground = "#f48771" },
        new StylingRule { Name = "warning keyword",
            Pattern = @"\b(?:warning|Warning|WARN|warn)\b",
            Foreground = "#e2c08d" },
        new StylingRule { Name = "success keyword",
            Pattern = @"\b(?:success|Success|OK|passed|PASSED|done|DONE)\b",
            Foreground = "#6a9955" },
    };
}
