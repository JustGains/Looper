using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Looper.Models;
using Looper.Services;

namespace Looper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ProjectViewModel>? ProjectAdded;
    public event EventHandler<ProjectViewModel>? ProjectRemoved;

    private readonly ConfigStore _configStore;

    public LoopSettings Settings { get; }

    public ObservableCollection<ProjectViewModel> Projects { get; } = new();

    private ProjectViewModel? _selectedProject;
    public ProjectViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (ReferenceEquals(_selectedProject, value)) return;
            _selectedProject = value;
            Settings.ActiveProject = value?.WorkingDirectory;
            SaveConfig();
            OnChanged();
        }
    }

    public bool CanCloseAny => Projects.Count > 1;

    // ---- tool/model/effort (shared across projects) ----
    public sealed record ToolOption(CliTool Tool, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<ToolOption> ToolOptions { get; } = new[]
    {
        new ToolOption(CliTool.ClaudeCode, "Claude Code"),
        new ToolOption(CliTool.Codex, "Codex"),
    };

    private static readonly IReadOnlyList<string> ClaudeModelSuggestions = new[]
    { "", "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001", "opus", "sonnet", "haiku" };
    private static readonly IReadOnlyList<string> ClaudeEffortSuggestions = new[]
    { "", "low", "medium", "high", "xhigh", "max" };
    private static readonly IReadOnlyList<string> CodexModelSuggestions = new[]
    { "", "gpt-5-codex", "gpt-5", "gpt-5.4", "o4-mini", "o3" };
    private static readonly IReadOnlyList<string> CodexEffortSuggestions = new[]
    { "", "low", "medium", "high" };

    public IReadOnlyList<string> ModelSuggestions =>
        Tool == CliTool.ClaudeCode ? ClaudeModelSuggestions : CodexModelSuggestions;
    public IReadOnlyList<string> EffortSuggestions =>
        Tool == CliTool.ClaudeCode ? ClaudeEffortSuggestions : CodexEffortSuggestions;

    public string ModelText
    {
        get => (Tool == CliTool.ClaudeCode ? Settings.ClaudeModel : Settings.CodexModel) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Tool == CliTool.ClaudeCode) Settings.ClaudeModel = v;
            else Settings.CodexModel = v;
            SaveConfig();
            OnChanged();
        }
    }

    public string EffortText
    {
        get => (Tool == CliTool.ClaudeCode ? Settings.ClaudeEffort : Settings.CodexEffort) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Tool == CliTool.ClaudeCode) Settings.ClaudeEffort = v;
            else Settings.CodexEffort = v;
            SaveConfig();
            OnChanged();
        }
    }

    public ToolOption SelectedToolOption
    {
        get => ToolOptions.First(o => o.Tool == Tool);
        set { if (value != null) Tool = value.Tool; }
    }

    private CliTool _tool;
    public CliTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            Settings.Tool = value;
            SaveConfig();
            OnChanged();
            OnChanged(nameof(SelectedToolOption));
            OnChanged(nameof(ModelSuggestions));
            OnChanged(nameof(EffortSuggestions));
            OnChanged(nameof(ModelText));
            OnChanged(nameof(EffortText));
        }
    }

    private int _timeoutSeconds;
    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set
        {
            if (_timeoutSeconds == value) return;
            _timeoutSeconds = value;
            Settings.TimeoutSeconds = value;
            SaveConfig();
            OnChanged();
        }
    }

    private int _maxIterations;
    public int MaxIterations
    {
        get => _maxIterations;
        set
        {
            if (_maxIterations == value) return;
            _maxIterations = value;
            Settings.MaxIterations = value;
            SaveConfig();
            OnChanged();
            foreach (var p in Projects) p.NotifyMaxIterChanged();
        }
    }

    public int TasksTabIndex
    {
        get => Settings.TasksTabIndex;
        set { if (Settings.TasksTabIndex == value) return; Settings.TasksTabIndex = value; SaveConfig(); OnChanged(); }
    }

    public bool AutoScrollConsole
    {
        get => Settings.AutoScrollConsole;
        set { if (Settings.AutoScrollConsole == value) return; Settings.AutoScrollConsole = value; SaveConfig(); OnChanged(); }
    }

    public bool WordWrapConsole
    {
        get => Settings.WordWrapConsole;
        set { if (Settings.WordWrapConsole == value) return; Settings.WordWrapConsole = value; SaveConfig(); OnChanged(); }
    }

    public bool CollapseToolCalls
    {
        get => Settings.CollapseToolCalls;
        set { if (Settings.CollapseToolCalls == value) return; Settings.CollapseToolCalls = value; SaveConfig(); OnChanged(); }
    }

    public bool KeepContext
    {
        get => Settings.KeepContext;
        set { if (Settings.KeepContext == value) return; Settings.KeepContext = value; SaveConfig(); OnChanged(); }
    }

    public bool RalphEnabled
    {
        get => Settings.RalphEnabled;
        set
        {
            if (Settings.RalphEnabled == value) return;
            Settings.RalphEnabled = value;
            SaveConfig();
            OnChanged();
            foreach (var p in Projects) p.NotifyMaxIterChanged();
        }
    }

    public bool AutoScrollTasks
    {
        get => Settings.AutoScrollTasks;
        set { if (Settings.AutoScrollTasks == value) return; Settings.AutoScrollTasks = value; SaveConfig(); OnChanged(); }
    }

    public ObservableCollection<string> RecentWorkingDirectories { get; } = new();

    public MainViewModel()
    {
        _configStore = new ConfigStore();
        Settings = _configStore.Load();
        _tool = Settings.Tool;
        _timeoutSeconds = Settings.TimeoutSeconds;
        _maxIterations = Settings.MaxIterations;
        SyncRecentList();
    }

    /// Called from MainWindow once it's ready to receive projects.
    public void InitializeTabs(string fallbackDir)
    {
        var seeds = Settings.OpenProjects.Where(Directory.Exists).ToList();
        if (seeds.Count == 0) seeds.Add(fallbackDir);

        foreach (var dir in seeds)
            AddProject(dir, selectIt: false, suppressSave: true);

        var match = Projects.FirstOrDefault(p =>
            string.Equals(p.WorkingDirectory, Settings.ActiveProject, StringComparison.OrdinalIgnoreCase));
        SelectedProject = match ?? Projects.FirstOrDefault();

        Settings.OpenProjects = Projects.Select(p => p.WorkingDirectory).ToList();
        Settings.ActiveProject = SelectedProject?.WorkingDirectory;
        SaveConfig();
    }

    public bool AddProject(string dir) => AddProject(dir, selectIt: true, suppressSave: false);

    private bool AddProject(string dir, bool selectIt, bool suppressSave)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try { if (!Directory.Exists(dir)) return false; } catch { return false; }

        var normalized = ConfigStore.Normalize(dir);
        var existing = Projects.FirstOrDefault(p =>
            string.Equals(p.WorkingDirectory, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (selectIt) SelectedProject = existing;
            return true;
        }

        var pvm = new ProjectViewModel(normalized, Settings);
        Projects.Add(pvm);
        ProjectAdded?.Invoke(this, pvm);
        _configStore.AddOpenProject(Settings, normalized);
        _configStore.PushRecent(Settings, normalized);
        SyncRecentList();
        OnChanged(nameof(CanCloseAny));
        if (selectIt) SelectedProject = pvm;
        if (!suppressSave) SaveConfig();
        return true;
    }

    public void CloseProject(ProjectViewModel p)
    {
        if (p == null || !Projects.Contains(p)) return;
        if (Projects.Count <= 1) return; // keep at least one

        var wasSelected = ReferenceEquals(SelectedProject, p);
        var index = Projects.IndexOf(p);
        p.Shutdown();
        Projects.Remove(p);
        ProjectRemoved?.Invoke(this, p);
        _configStore.RemoveOpenProject(Settings, p.WorkingDirectory);
        OnChanged(nameof(CanCloseAny));

        if (wasSelected)
            SelectedProject = Projects[Math.Max(0, Math.Min(index - 1, Projects.Count - 1))];

        SaveConfig();
    }

    private void SyncRecentList()
    {
        var desired = Settings.RecentWorkingDirectories;
        for (int i = RecentWorkingDirectories.Count - 1; i >= 0; i--)
        {
            if (!desired.Any(d => string.Equals(d, RecentWorkingDirectories[i], StringComparison.OrdinalIgnoreCase)))
                RecentWorkingDirectories.RemoveAt(i);
        }
        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            int cur = -1;
            for (int j = 0; j < RecentWorkingDirectories.Count; j++)
            {
                if (string.Equals(RecentWorkingDirectories[j], d, StringComparison.OrdinalIgnoreCase))
                { cur = j; break; }
            }
            if (cur < 0) RecentWorkingDirectories.Insert(i, d);
            else if (cur != i) RecentWorkingDirectories.Move(cur, i);
        }
    }

    public void SaveConfig() => _configStore.Save(Settings);

    public void OpenConfigInNotepad()
    {
        var path = _configStore.Path;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(path)) _configStore.Save(Settings);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    public string ConfigPath => _configStore.Path;

    public void SaveWindowBounds(double left, double top, double width, double height)
    {
        Settings.WindowLeft = left;
        Settings.WindowTop = top;
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
        SaveConfig();
    }

    public void Shutdown()
    {
        Settings.OpenProjects = Projects.Select(p => p.WorkingDirectory).ToList();
        Settings.ActiveProject = SelectedProject?.WorkingDirectory;
        SaveConfig();
        foreach (var p in Projects.ToList()) p.Shutdown();
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
