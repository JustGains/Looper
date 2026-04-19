using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Looper.Models;
using Looper.Services;

namespace Looper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly PromptStore _promptStore;
    private readonly TasksFileService _tasksFile;
    private readonly LoopRunner _loopRunner;
    private readonly ConfigStore _configStore;
    private readonly DispatcherTimer _tasksSaveDebounce;
    private readonly DispatcherTimer _uiTick;
    private readonly DispatcherTimer _workDirDebounce;
    private DateTime? _iterStartUtc;
    private DateTime? _totalStartUtc;
    private bool _suppressTasksSave;

    public LoopSettings Settings { get; private set; }

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
    {
        "", "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001",
        "opus", "sonnet", "haiku",
    };
    private static readonly IReadOnlyList<string> ClaudeEffortSuggestions = new[]
    {
        "", "low", "medium", "high", "xhigh", "max",
    };
    private static readonly IReadOnlyList<string> CodexModelSuggestions = new[]
    {
        "", "gpt-5-codex", "gpt-5", "gpt-5.4", "o4-mini", "o3",
    };
    private static readonly IReadOnlyList<string> CodexEffortSuggestions = new[]
    {
        "", "low", "medium", "high",
    };

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

    private string _prompt = "";
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (_prompt == value) return;
            _prompt = value;
            _promptStore.SavePromptDebounced(value);
            OnChanged();
            if (IsRunning && _promptAtLastInjection is not null && value != _promptAtLastInjection)
                PromptPendingChange = true;
        }
    }

    private string? _promptAtLastInjection;

    private bool _promptPendingChange;
    public bool PromptPendingChange
    {
        get => _promptPendingChange;
        private set { if (_promptPendingChange != value) { _promptPendingChange = value; OnChanged(); } }
    }

    private string _tasksText = "";
    public string TasksText
    {
        get => _tasksText;
        set
        {
            if (_tasksText == value) return;
            _tasksText = value;
            OnChanged();
            if (!_suppressTasksSave)
            {
                _tasksSaveDebounce.Stop();
                _tasksSaveDebounce.Start();
            }
        }
    }

    private string _workingDirectory = "";
    public string WorkingDirectory => _workingDirectory;

    private string _workingDirectoryInput = "";
    public string WorkingDirectoryInput
    {
        get => _workingDirectoryInput;
        set
        {
            var v = value ?? "";
            if (_workingDirectoryInput == v) return;
            _workingDirectoryInput = v;
            OnChanged();
            _workDirDebounce.Stop();
            if (!string.IsNullOrWhiteSpace(v)
                && !string.Equals(v.Trim(), _workingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _workDirDebounce.Start();
            }
        }
    }

    public void CommitWorkingDirectoryNow()
    {
        _workDirDebounce.Stop();
        TryApplyWorkingDirectory(_workingDirectoryInput);
    }

    private void TryApplyWorkingDirectory(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var normalized = candidate.Trim();
        if (string.Equals(normalized, _workingDirectory, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!Directory.Exists(normalized))
            {
                ConsoleAppend?.Invoke(this,
                    $"[looper] working directory does not exist: {normalized}\n");
                return;
            }
        }
        catch (Exception ex)
        {
            ConsoleAppend?.Invoke(this,
                $"[looper] cannot access working directory: {normalized} — {ex.Message}\n");
            return;
        }
        SwitchWorkingDirectory(normalized);
    }

    public ObservableCollection<string> RecentWorkingDirectories { get; } = new();

    public event EventHandler? WorkingDirectoryChanged;

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
            OnChanged(nameof(IterationLabel));
        }
    }

    private int _currentIteration;
    public int CurrentIteration
    {
        get => _currentIteration;
        private set { if (_currentIteration != value) { _currentIteration = value; OnChanged(); OnChanged(nameof(IterationLabel)); } }
    }

    public string IterationLabel => $"Iteration: {CurrentIteration} / {MaxIterations}";

    public string IterationElapsedText => FormatElapsed(_iterStartUtc);
    public string TotalElapsedText => FormatElapsed(_totalStartUtc);

    private static string FormatElapsed(DateTime? startUtc)
    {
        if (startUtc is null) return "--:--";
        var t = DateTime.UtcNow - startUtc.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    public int TasksTabIndex
    {
        get => Settings.TasksTabIndex;
        set
        {
            if (Settings.TasksTabIndex == value) return;
            Settings.TasksTabIndex = value;
            SaveConfig();
            OnChanged();
        }
    }

    public bool AutoScrollConsole
    {
        get => Settings.AutoScrollConsole;
        set
        {
            if (Settings.AutoScrollConsole == value) return;
            Settings.AutoScrollConsole = value;
            SaveConfig();
            OnChanged();
        }
    }

    public bool WordWrapConsole
    {
        get => Settings.WordWrapConsole;
        set
        {
            if (Settings.WordWrapConsole == value) return;
            Settings.WordWrapConsole = value;
            SaveConfig();
            OnChanged();
        }
    }

    public bool CollapseToolCalls
    {
        get => Settings.CollapseToolCalls;
        set
        {
            if (Settings.CollapseToolCalls == value) return;
            Settings.CollapseToolCalls = value;
            SaveConfig();
            OnChanged();
        }
    }

    public bool KeepContext
    {
        get => Settings.KeepContext;
        set
        {
            if (Settings.KeepContext == value) return;
            Settings.KeepContext = value;
            SaveConfig();
            OnChanged();
        }
    }

    public bool AutoScrollTasks
    {
        get => Settings.AutoScrollTasks;
        set
        {
            if (Settings.AutoScrollTasks == value) return;
            Settings.AutoScrollTasks = value;
            SaveConfig();
            OnChanged();
        }
    }

    private string _status = "Idle";
    public string Status
    {
        get => _status;
        private set { if (_status != value) { _status = value; OnChanged(); } }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (_isRunning != value) { _isRunning = value; OnChanged(); OnChanged(nameof(StartStopText)); } }
    }

    public string StartStopText => IsRunning ? "Stop" : "Start";

    public event EventHandler<string>? ConsoleAppend;

    public MainViewModel(string fallbackWorkingDirectory)
    {
        _configStore = new ConfigStore();
        Settings = _configStore.Load();

        var initialWorkingDirectory = ResolveInitialDir(fallbackWorkingDirectory);
        Settings.WorkingDirectory = initialWorkingDirectory;
        SyncRecentList();

        _promptStore = new PromptStore(Settings);

        _tasksFile = new TasksFileService();
        _tasksSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tasksSaveDebounce.Tick += (_, _) =>
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        };

        _tasksFile.ExternalChange += (_, text) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyExternalTasks(text));
        };

        _workDirDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _workDirDebounce.Tick += (_, _) =>
        {
            _workDirDebounce.Stop();
            TryApplyWorkingDirectory(_workingDirectoryInput);
        };

        _uiTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTick.Tick += (_, _) =>
        {
            OnChanged(nameof(IterationElapsedText));
            OnChanged(nameof(TotalElapsedText));
        };

        _loopRunner = new LoopRunner(new CliProcessRunner());
        _loopRunner.Output += (_, chunk) => Application.Current?.Dispatcher.BeginInvoke(() => ConsoleAppend?.Invoke(this, chunk));
        _loopRunner.Status += (_, s) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Status = s;
            IsRunning = s is "Running" or "Restarting" or "Killing";
            if (!IsRunning)
            {
                _uiTick.Stop();
                OnChanged(nameof(IterationElapsedText));
                OnChanged(nameof(TotalElapsedText));
            }
        });
        _loopRunner.IterationChanged += (_, t) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CurrentIteration = t.current;
            _iterStartUtc = DateTime.UtcNow;
            OnChanged(nameof(IterationElapsedText));
        });
        _loopRunner.PromptInjected += (_, p) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _promptAtLastInjection = p;
            PromptPendingChange = false;
        });

        _workingDirectory = Settings.WorkingDirectory;
        _workingDirectoryInput = Settings.WorkingDirectory;
        _tool = Settings.Tool;
        _timeoutSeconds = Settings.TimeoutSeconds;
        _maxIterations = Settings.MaxIterations;
        _prompt = _promptStore.LoadPrompt();
        _tasksFile.Watch(Settings.TasksFile);
        _tasksText = _tasksFile.Load();

        _configStore.PushRecent(Settings, _workingDirectory);
        SaveConfig();
        SyncRecentList();
    }

    private string ResolveInitialDir(string fallback)
    {
        foreach (var d in Settings.RecentWorkingDirectories)
        {
            try { if (Directory.Exists(d)) return d; } catch { }
        }
        return fallback;
    }

    // Smart sync preserves ObservableCollection item identity so the ComboBox's
    // selected item / Text binding doesn't get reset mid-switch.
    private void SyncRecentList()
    {
        var desired = Settings.RecentWorkingDirectories;

        for (int i = RecentWorkingDirectories.Count - 1; i >= 0; i--)
        {
            var cur = RecentWorkingDirectories[i];
            if (!desired.Any(d => string.Equals(d, cur, StringComparison.OrdinalIgnoreCase)))
                RecentWorkingDirectories.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            int curIdx = -1;
            for (int j = 0; j < RecentWorkingDirectories.Count; j++)
            {
                if (string.Equals(RecentWorkingDirectories[j], d, StringComparison.OrdinalIgnoreCase))
                {
                    curIdx = j; break;
                }
            }
            if (curIdx < 0)
                RecentWorkingDirectories.Insert(i, d);
            else if (curIdx != i)
                RecentWorkingDirectories.Move(curIdx, i);
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
        catch (Exception ex)
        {
            ConsoleAppend?.Invoke(this, $"[looper] failed to open config: {ex.Message}\n");
        }
    }

    public string ConfigPath => _configStore.Path;

    private void SwitchWorkingDirectory(string newDir)
    {
        // Flush any pending saves to the OLD location first.
        if (_tasksSaveDebounce.IsEnabled)
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        }
        _promptStore.FlushPrompt();
        _workDirDebounce.Stop();

        _workingDirectory = newDir;
        _workingDirectoryInput = newDir;

        // Global config stays; only the transient working dir changes.
        Settings.WorkingDirectory = newDir;
        _promptStore.UpdateSettings(Settings);

        // Push into recents.
        _configStore.PushRecent(Settings, newDir);
        SaveConfig();
        SyncRecentList();

        // Reload per-dir artifacts (prompt + tasks only).
        _suppressTasksSave = true;
        try
        {
            _prompt = _promptStore.LoadPrompt();
            _tasksFile.Watch(Settings.TasksFile);
            _tasksText = _tasksFile.Load();
        }
        finally { _suppressTasksSave = false; }

        OnChanged(nameof(WorkingDirectory));
        OnChanged(nameof(WorkingDirectoryInput));
        OnChanged(nameof(Prompt));
        OnChanged(nameof(TasksText));
        OnChanged(nameof(TasksFile));
        OnChanged(nameof(PromptFile));
        OnChanged(nameof(Tool));
        OnChanged(nameof(SelectedToolOption));
        OnChanged(nameof(TimeoutSeconds));
        OnChanged(nameof(MaxIterations));
        OnChanged(nameof(IterationLabel));
        OnChanged(nameof(ModelText));
        OnChanged(nameof(EffortText));
        OnChanged(nameof(ModelSuggestions));
        OnChanged(nameof(EffortSuggestions));
        OnChanged(nameof(TasksTabIndex));
        OnChanged(nameof(AutoScrollConsole));
        OnChanged(nameof(WordWrapConsole));
        OnChanged(nameof(CollapseToolCalls));

        WorkingDirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyExternalTasks(string text)
    {
        if (_tasksText == text) return;
        _suppressTasksSave = true;
        try { TasksText = text; }
        finally { _suppressTasksSave = false; }
    }

    public async Task ToggleStartStopAsync()
    {
        if (IsRunning)
        {
            _loopRunner.Stop();
            return;
        }
        _promptStore.FlushPrompt();
        _promptAtLastInjection = null;
        PromptPendingChange = false;
        IsRunning = true;
        Status = "Running";
        CurrentIteration = 0;
        _totalStartUtc = DateTime.UtcNow;
        _iterStartUtc = DateTime.UtcNow;
        _uiTick.Start();
        OnChanged(nameof(TotalElapsedText));
        OnChanged(nameof(IterationElapsedText));
        try
        {
            await _loopRunner.RunAsync(() => _prompt, Settings);
        }
        finally
        {
            IsRunning = false;
            _uiTick.Stop();
            _promptAtLastInjection = null;
            PromptPendingChange = false;
            OnChanged(nameof(TotalElapsedText));
            OnChanged(nameof(IterationElapsedText));
        }
    }

    public string PromptFile => Settings.PromptFile;
    public string TasksFile => Settings.TasksFile;

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
        _loopRunner.Stop();
        _promptStore.FlushPrompt();
        if (_tasksSaveDebounce.IsEnabled)
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        }
        _tasksFile.Dispose();
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
