using System.Text.Json.Serialization;
using Looper.Services;

namespace Looper.Models;

public enum CliTool
{
    ClaudeCode,
    Codex,
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

    // UI
    public int TasksTabIndex { get; set; } = 1;
    public bool AutoScrollConsole { get; set; } = true;
    public bool WordWrapConsole { get; set; } = true;
    public bool CollapseToolCalls { get; set; } = false;
    public bool KeepContext { get; set; } = false;
    public bool AutoScrollTasks { get; set; } = false;

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
