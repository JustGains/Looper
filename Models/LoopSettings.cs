using System.Text.Json.Serialization;
using JustCode.Services;

namespace JustCode.Models;

public enum CliTool
{
    ClaudeCode,
    Codex,
    Pi,
}

public sealed class LoopSettings
{
    // Engine
    public CliTool Tool { get; set; } = CliTool.ClaudeCode;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxIterations { get; set; } = 5;
    public bool RalphEnabled { get; set; } = true;
    public string? ClaudeModel { get; set; }
    public string? ClaudeEffort { get; set; }
    public string? CodexModel { get; set; }
    public string? CodexEffort { get; set; }
    public string? PiModel { get; set; }
    public string? PiThinking { get; set; }
    /// Favourited models per-tool (starred) so they float to the top of the
    /// picker and survive app restarts.
    public List<string> ClaudeFavoriteModels { get; set; } = new();
    public List<string> CodexFavoriteModels { get; set; } = new();
    public List<string> PiFavoriteModels { get; set; } = new();
    /// Cached list of pi models most recently returned by `pi --list-models`
    /// so the UI can populate without blocking on a subprocess each launch.
    public List<string> PiModelCache { get; set; } = new();

    // UI
    public int TasksTabIndex { get; set; } = 1;
    public int ConsoleTabIndex { get; set; } = 0;
    public bool AutoScrollConsole { get; set; } = true;
    public bool WordWrapConsole { get; set; } = true;
    public bool CollapseToolCalls { get; set; } = false;
    public bool KeepContext { get; set; } = false;
    public bool AutoScrollTasks { get; set; } = false;

    // Terminal
    /// Id of the default shell profile (see ShellDetector). Empty/unknown
    /// resolves to the first detected shell — pwsh > powershell > cmd.
    public string DefaultShellId { get; set; } = "";

    // Window bounds
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    // Projects (tabs)
    public List<string> OpenProjects { get; set; } = new();
    public string? ActiveProject { get; set; }

    // Folder-picker history (unrelated to OpenProjects)
    public List<string> RecentWorkingDirectories { get; set; } = new();

    // Styling rules for the streaming console
    public List<StylingRule> StylingRules { get; set; } = new();

    // Legacy migration — read once from old config, never written.
    [JsonInclude] public string? LastWorkingDirectory { get; set; }
    [JsonInclude] public string? WorkingDirectory { get; set; }
}
