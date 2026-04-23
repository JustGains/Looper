using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using JustCode.Models;
using JustCode.Services;

namespace JustCode.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ProjectViewModel>? ProjectAdded;
    public event EventHandler<ProjectViewModel>? ProjectRemoved;

    private readonly ConfigStore _configStore;

    public LoopSettings Settings { get; }

    public ModelPickerViewModel ModelPicker { get; }

    public ObservableCollection<ProjectViewModel> Projects { get; } = new();

    private ProjectViewModel? _selectedProject;
    public ProjectViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (ReferenceEquals(_selectedProject, value)) return;
            // Deactivate tab polling on the project we're leaving so its
            // Git watcher / timer stop immediately — multiple projects would
            // otherwise all keep ticking in the background.
            _selectedProject?.SetIsSelected(false);
            _selectedProject = value;
            value?.SetIsSelected(true);
            Settings.ActiveProject = value?.WorkingDirectory;
            SaveConfig();
            OnChanged();
        }
    }

    public bool CanCloseAny => Projects.Count > 1;

    // ---- tool/model/effort lookups (static; conversations reference these) ----
    public sealed record ToolOption(CliTool Tool, string Name)
    {
        public override string ToString() => Name;
    }

    public static IReadOnlyList<ToolOption> AllToolOptions { get; } = new[]
    {
        new ToolOption(CliTool.ClaudeCode, "Claude Code"),
        new ToolOption(CliTool.Codex, "Codex"),
        new ToolOption(CliTool.Pi, "Pi"),
    };

    public static IReadOnlyList<string> ClaudeModels { get; } = new[]
    { "", "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001", "opus", "sonnet", "haiku" };
    public static IReadOnlyList<string> ClaudeEfforts { get; } = new[]
    { "", "low", "medium", "high", "xhigh", "max" };
    public static IReadOnlyList<string> CodexModels { get; } = new[]
    { "", "gpt-5-codex", "gpt-5", "gpt-5.4", "o4-mini", "o3" };
    public static IReadOnlyList<string> CodexEfforts { get; } = new[]
    { "", "low", "medium", "high" };
    /// Seed list used until `pi --list-models` populates the live cache.
    /// Covers the most common providers/models pi ships with. Users with API
    /// keys set will see their live model list appended/replacing this.
    public static IReadOnlyList<string> PiModelsSeed { get; } = new[]
    {
        "",
        // Anthropic
        "anthropic/claude-opus-4-7", "anthropic/claude-sonnet-4-6", "anthropic/claude-haiku-4-5",
        // OpenAI
        "openai/gpt-5", "openai/gpt-5-codex", "openai/gpt-4o", "openai/gpt-4o-mini", "openai/o4-mini", "openai/o3",
        // Google
        "google/gemini-2.5-pro", "google/gemini-2.5-flash",
        // xAI
        "xai/grok-4", "xai/grok-3",
        // Mistral / Groq / Cerebras / etc
        "mistral/mistral-large-latest", "groq/llama-3.3-70b-versatile",
        "cerebras/llama-3.3-70b", "openrouter/deepseek-chat",
        // Ollama (local)
        "ollama/llama3.1", "ollama/qwen2.5-coder",
    };
    public static IReadOnlyList<string> PiEfforts { get; } = new[]
    { "", "off", "minimal", "low", "medium", "high", "xhigh" };

    public IReadOnlyList<ToolOption> ToolOptions => AllToolOptions;

    // ---- shell-level UI preferences ----
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
        SyncRecentList();
        ModelPicker = new ModelPickerViewModel(Settings, SaveConfig, Settings.Tool);
        // Best-effort live refresh (pi only); no-op for Claude/Codex and
        // silent-fail if pi isn't installed.
        _ = ModelPicker.RefreshFromCliAsync();
    }

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
        if (Projects.Count <= 1) return;

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

    // Debounced + coalesced config save. UI tabs/window-state/prefs all
    // cascade through this path, and the raw JSON write was being done
    // synchronously on every ripple. 400 ms window is enough to collapse a
    // burst (project switch, tab toggle, combo box pick) into one write.
    private System.Windows.Threading.DispatcherTimer? _saveDebounce;
    public void SaveConfig()
    {
        if (_saveDebounce == null)
        {
            _saveDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveDebounce.Tick += (_, _) => { _saveDebounce!.Stop(); _configStore.Save(Settings); };
        }
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void FlushSaveConfig()
    {
        if (_saveDebounce?.IsEnabled == true)
        {
            _saveDebounce.Stop();
            _configStore.Save(Settings);
        }
    }

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
        // Flush any pending debounced writes synchronously so we don't exit
        // before the last edits land on disk.
        _configStore.Save(Settings);
        FlushSaveConfig();
        foreach (var p in Projects.ToList()) p.Shutdown();
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
