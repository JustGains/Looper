using System.IO;
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
    // Transient: the current working directory. Also saved under LastWorkingDirectory.
    [JsonIgnore]
    public string WorkingDirectory { get; set; } = "";

    // Engine
    public CliTool Tool { get; set; } = CliTool.ClaudeCode;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxIterations { get; set; } = 5;
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

    // Dir history
    public List<string> RecentWorkingDirectories { get; set; } = new();
    public string? LastWorkingDirectory { get; set; }

    // Styling rules (also shown in the same config file)
    public List<StylingRule> StylingRules { get; set; } = new();

    // Derived paths (per-working-dir runtime files)
    [JsonIgnore] public string LooperDir => Path.Combine(WorkingDirectory, ".looper");
    [JsonIgnore] public string PromptFile => Path.Combine(LooperDir, "prompt.txt");
    [JsonIgnore] public string TasksFile => Path.Combine(LooperDir, "tasks.md");
}
