using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Looper.Models;
using Looper.Services;

namespace Looper.ViewModels;

public sealed class ConversationViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ConsoleAppend;

    private readonly string _workingDirectory;
    private readonly ConversationSettings _settings;
    private readonly PromptStore _promptStore;
    private readonly TasksFileService _tasksFile;
    private readonly LoopRunner _loopRunner;
    private readonly DispatcherTimer _tasksSaveDebounce;
    private readonly DispatcherTimer _uiTick;
    private DateTime? _iterStartUtc;
    private DateTime? _totalStartUtc;
    private bool _suppressTasksSave;
    private string? _promptAtLastInjection;

    private readonly Action _persistSettings;

    public string Id => _settings.Id;
    public string WorkingDirectory => _workingDirectory;
    public string PromptFile => ConversationStore.PromptFile(_workingDirectory, Id);
    public string TasksFile => ConversationStore.TasksFile(_workingDirectory, Id);

    public FlowDocument ConsoleDocument { get; }
    public Paragraph ConsoleParagraph { get; }

    public string Name
    {
        get => _settings.Name;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? Id : value.Trim();
            if (_settings.Name == v) return;
            _settings.Name = v;
            PersistSettings();
            OnChanged();
        }
    }

    private bool _isEditingName;
    public bool IsEditingName
    {
        get => _isEditingName;
        private set { if (_isEditingName != value) { _isEditingName = value; OnChanged(); } }
    }

    private string? _nameBeforeEdit;

    public void BeginRename()
    {
        if (IsEditingName) return;
        _nameBeforeEdit = _settings.Name;
        IsEditingName = true;
    }

    public void CommitRename() => IsEditingName = false;

    public void CancelRename()
    {
        if (_nameBeforeEdit != null) Name = _nameBeforeEdit;
        _nameBeforeEdit = null;
        IsEditingName = false;
    }

    // Engine settings (persisted to conversation's settings.json)
    public CliTool Tool
    {
        get => _settings.Tool;
        set
        {
            if (_settings.Tool == value) return;
            _settings.Tool = value;
            PersistSettings();
            OnChanged();
            OnChanged(nameof(SelectedToolOption));
            OnChanged(nameof(ModelText));
            OnChanged(nameof(EffortText));
            OnChanged(nameof(ModelSuggestions));
            OnChanged(nameof(EffortSuggestions));
        }
    }

    public IReadOnlyList<MainViewModel.ToolOption> ToolOptions => MainViewModel.AllToolOptions;

    public MainViewModel.ToolOption SelectedToolOption
    {
        get => MainViewModel.AllToolOptions.First(o => o.Tool == Tool);
        set { if (value != null) Tool = value.Tool; }
    }

    public IReadOnlyList<string> ModelSuggestions =>
        Tool == CliTool.ClaudeCode ? MainViewModel.ClaudeModels : MainViewModel.CodexModels;
    public IReadOnlyList<string> EffortSuggestions =>
        Tool == CliTool.ClaudeCode ? MainViewModel.ClaudeEfforts : MainViewModel.CodexEfforts;

    public string ModelText
    {
        get => (Tool == CliTool.ClaudeCode ? _settings.ClaudeModel : _settings.CodexModel) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Tool == CliTool.ClaudeCode) _settings.ClaudeModel = v;
            else _settings.CodexModel = v;
            PersistSettings();
            OnChanged();
        }
    }

    public string EffortText
    {
        get => (Tool == CliTool.ClaudeCode ? _settings.ClaudeEffort : _settings.CodexEffort) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Tool == CliTool.ClaudeCode) _settings.ClaudeEffort = v;
            else _settings.CodexEffort = v;
            PersistSettings();
            OnChanged();
        }
    }

    public int TimeoutSeconds
    {
        get => _settings.TimeoutSeconds;
        set
        {
            if (_settings.TimeoutSeconds == value) return;
            _settings.TimeoutSeconds = value;
            PersistSettings();
            OnChanged();
        }
    }

    public int MaxIterations
    {
        get => _settings.MaxIterations;
        set
        {
            if (_settings.MaxIterations == value) return;
            _settings.MaxIterations = value;
            PersistSettings();
            OnChanged();
            OnChanged(nameof(IterationLabel));
        }
    }

    public bool RalphEnabled
    {
        get => _settings.RalphEnabled;
        set
        {
            if (_settings.RalphEnabled == value) return;
            _settings.RalphEnabled = value;
            PersistSettings();
            OnChanged();
            OnChanged(nameof(IterationLabel));
        }
    }

    public bool KeepContext
    {
        get => _settings.KeepContext;
        set
        {
            if (_settings.KeepContext == value) return;
            _settings.KeepContext = value;
            PersistSettings();
            OnChanged();
        }
    }

    // ---- per-conv runtime state ----
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

    private int _currentIteration;
    public int CurrentIteration
    {
        get => _currentIteration;
        private set { if (_currentIteration != value) { _currentIteration = value; OnChanged(); OnChanged(nameof(IterationLabel)); } }
    }

    public string IterationLabel =>
        $"Iteration: {CurrentIteration} / {(_settings.RalphEnabled ? _settings.MaxIterations : 1)}";
    public string IterationElapsedText => FormatElapsed(_iterStartUtc);
    public string TotalElapsedText => FormatElapsed(_totalStartUtc);

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
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnChanged();
            OnChanged(nameof(StartStopText));
        }
    }

    public string StartStopText => IsRunning ? "Stop" : "Start";

    private string? _currentSessionId;
    public string? CurrentSessionId
    {
        get => _currentSessionId;
        private set
        {
            if (_currentSessionId == value) return;
            _currentSessionId = value;
            OnChanged();
            OnChanged(nameof(HasSession));
            OnChanged(nameof(SessionShort));
        }
    }

    public bool HasSession => !string.IsNullOrEmpty(_currentSessionId);
    public string SessionShort
    {
        get
        {
            var s = _currentSessionId;
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 12 ? s.Substring(0, 8) + "…" + s.Substring(s.Length - 4) : s;
        }
    }

    private long _inputTokens, _outputTokens, _cachedTokens;
    public long InputTokens { get => _inputTokens; private set { if (_inputTokens != value) { _inputTokens = value; OnChanged(); OnChanged(nameof(TokenSummary)); } } }
    public long OutputTokens { get => _outputTokens; private set { if (_outputTokens != value) { _outputTokens = value; OnChanged(); OnChanged(nameof(TokenSummary)); } } }
    public long CachedTokens { get => _cachedTokens; private set { if (_cachedTokens != value) { _cachedTokens = value; OnChanged(); OnChanged(nameof(TokenSummary)); } } }

    public string TokenSummary
    {
        get
        {
            if (_inputTokens == 0 && _outputTokens == 0) return "";
            var s = $"{_inputTokens:N0} in · {_outputTokens:N0} out";
            if (_cachedTokens > 0) s += $" · {_cachedTokens:N0} cached";
            return s;
        }
    }

    public ConversationViewModel(string workingDirectory, ConversationSettings settings, Action persistSettings)
    {
        _workingDirectory = workingDirectory;
        _settings = settings;
        _persistSettings = persistSettings;

        ConsoleParagraph = new Paragraph { Margin = new Thickness(0), TextIndent = 0 };
        ConsoleDocument = new FlowDocument(ConsoleParagraph)
        {
            PageWidth = 6000,
            Background = System.Windows.Media.Brushes.Transparent,
        };

        var convDir = ConversationStore.ConversationDir(workingDirectory, settings.Id);
        Directory.CreateDirectory(convDir);
        _promptStore = new PromptStore(convDir, PromptFile);
        _tasksFile = new TasksFileService();

        _tasksSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tasksSaveDebounce.Tick += (_, _) =>
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        };

        _tasksFile.ExternalChange += (_, text) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyExternalTasks(text));

        _uiTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTick.Tick += (_, _) =>
        {
            OnChanged(nameof(IterationElapsedText));
            OnChanged(nameof(TotalElapsedText));
        };

        _loopRunner = new LoopRunner(new CliProcessRunner());
        _loopRunner.Output += (_, chunk) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ConsoleAppend?.Invoke(this, chunk));
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
        _loopRunner.SessionCaptured += (_, sid) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CurrentSessionId = sid;
        });
        _loopRunner.TokenUsageReported += (_, u) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Result events carry per-turn usage — accumulate across iterations.
            InputTokens += u.input;
            OutputTokens += u.output;
            CachedTokens += u.cached;
        });

        _prompt = _promptStore.LoadPrompt();
        _tasksFile.Watch(TasksFile);
        _tasksText = _tasksFile.Load();
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
        InputTokens = 0;
        OutputTokens = 0;
        CachedTokens = 0;
        IsRunning = true;
        Status = "Running";
        CurrentIteration = 0;
        _totalStartUtc = DateTime.UtcNow;
        _iterStartUtc = DateTime.UtcNow;
        _uiTick.Start();
        OnChanged(nameof(TotalElapsedText));
        OnChanged(nameof(IterationElapsedText));
        OnChanged(nameof(IterationLabel));
        try
        {
            await _loopRunner.RunAsync(() => _prompt, _settings, _workingDirectory);
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

    public void StopIfRunning()
    {
        if (IsRunning) _loopRunner.Stop();
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

    public void Dispose() => Shutdown();

    private void PersistSettings() => _persistSettings();

    private static string FormatElapsed(DateTime? startUtc)
    {
        if (startUtc is null) return "--:--";
        var t = DateTime.UtcNow - startUtc.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
