using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using JustCode.Models;
using JustCode.Services;

namespace JustCode.ViewModels;

public sealed class QueuedChatMessage : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = Guid.NewGuid().ToString("N");

    // Cached event args — these property names fire on every queue edit, so
    // pre-allocating the PropertyChangedEventArgs once per property saves
    // N-per-event allocations on ObservableCollection refresh and typing.
    private static readonly PropertyChangedEventArgs TextArgs = new(nameof(Text));
    private static readonly PropertyChangedEventArgs TooltipArgs = new(nameof(TooltipText));
    private static readonly PropertyChangedEventArgs ActiveArgs = new(nameof(IsActive));
    private static readonly PropertyChangedEventArgs HeadArgs = new(nameof(IsHead));
    private static readonly PropertyChangedEventArgs FirstArgs = new(nameof(IsFirst));
    private static readonly PropertyChangedEventArgs LastArgs = new(nameof(IsLast));

    private string _text;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            var h = PropertyChanged;
            if (h == null) return;
            h(this, TextArgs);
            h(this, TooltipArgs);
        }
    }

    /// Hover tooltip for the chip: the full message text followed by a
    /// dim footer with char + token estimate so the user can size up the
    /// payload before it's injected.
    public string TooltipText
    {
        get
        {
            var t = _text ?? "";
            int chars = t.Length;
            if (chars == 0) return t;
            var tokenPart = TextStats.FormatTokens(TextStats.ApproxTokens(chars));
            var stats = tokenPart.Length > 0
                ? $"{chars:N0} chars · {tokenPart}"
                : $"{chars:N0} chars";
            return $"{t}\n\n— {stats}";
        }
    }

    /// True when this chip is the one currently loaded into the chat input
    /// via Up/Down recall or click-to-edit. The XAML binds an accent border
    /// to this flag so the user sees which chip their edits are flowing into.
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, ActiveArgs);
        }
    }

    /// True for the chip at index 0 — i.e. the next one the LoopRunner will
    /// inject. The XAML uses this to render a small "NEXT" pill so the user
    /// sees the dequeue order at a glance.
    private bool _isHead;
    public bool IsHead
    {
        get => _isHead;
        internal set
        {
            if (_isHead == value) return;
            _isHead = value;
            PropertyChanged?.Invoke(this, HeadArgs);
        }
    }

    /// True when this chip is at index 0. The XAML binds the ▲ (move up)
    /// button's IsEnabled to `!IsFirst` so the button auto-disables on the
    /// head chip without needing any code-behind guard. Semantically
    /// overlaps with IsHead but IsHead is suppressed when there's only
    /// one chip — the boundary buttons care about raw position only.
    private bool _isFirst;
    public bool IsFirst
    {
        get => _isFirst;
        internal set
        {
            if (_isFirst == value) return;
            _isFirst = value;
            PropertyChanged?.Invoke(this, FirstArgs);
        }
    }

    /// True when this chip is at index `Count - 1`. Bound to the ▼ button's
    /// IsEnabled so reordering controls self-indicate the boundaries.
    private bool _isLast;
    public bool IsLast
    {
        get => _isLast;
        internal set
        {
            if (_isLast == value) return;
            _isLast = value;
            PropertyChanged?.Invoke(this, LastArgs);
        }
    }

    public QueuedChatMessage(string text) { _text = text; }
}

public sealed class ConversationViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ConsoleAppend;

    private readonly string _workingDirectory;
    private readonly ConversationSettings _settings;
    private readonly PromptStore _promptStore;
    private readonly TasksFileService _tasksFile;
    private readonly LoopRunner _loopRunner;
    private readonly ConsoleLogStore _consoleLog;
    private readonly Func<LoopSettings> _appSettingsProvider;
    private string? _pendingConsoleHistory;
    private readonly DispatcherTimer _tasksSaveDebounce;
    private readonly DispatcherTimer _uiTick;
    private DateTime? _iterStartUtc;
    private DateTime? _totalStartUtc;
    private bool _isDisposed;
    private bool _suppressTasksSave;
    private string? _promptAtLastInjection;

    private readonly Action _persistSettings;

    // Yolo mode (Task Manager checkbox unchecked): one terminal panel per
    // conversation, holding a single session that runs the selected tool's
    // interactive yolo command directly. Created lazily on first toggle.
    private TerminalPanelViewModel? _yoloPanel;
    private TerminalSessionViewModel? _yoloSession;
    private CliTool _yoloSessionTool;
    private SessionFileWatcher? _yoloSessionWatcher;
    private DispatcherTimer? _yoloBusyTimer;
    private bool _yoloBusy;
    // Output sliding window used to detect "couldn't resume session" errors
    // before the agent exits — when the next exit fires we drop the dead
    // session id and respawn fresh instead of looping on a stale --resume.
    private readonly StringBuilder _yoloRecentOutput = new();
    private bool _yoloResumeFailureSeen;
    private DateTime _yoloLastStartUtc = DateTime.MinValue;
    private int _yoloConsecutiveQuickExits;
    private const int YoloMaxConsecutiveQuickExits = 3;
    private static readonly TimeSpan YoloQuickExitWindow = TimeSpan.FromSeconds(2);

    public string Id => _settings.Id;
    public string WorkingDirectory => _workingDirectory;
    public string PromptFile => ConversationStore.PromptFile(_workingDirectory, Id);
    public string TasksFile => ConversationStore.TasksFile(_workingDirectory, Id);

    public FlowDocument ConsoleDocument { get; }
    public Paragraph ConsoleParagraph { get; }
    public FlowDocument ConversationConsoleDocument { get; }
    public Paragraph ConversationConsoleParagraph { get; }
    public FlowDocument ToolConsoleDocument { get; }
    public Paragraph ToolConsoleParagraph { get; }

    public string Name
    {
        get => _settings.Name;
        set
        {
            if (_isDisposed) return;
            var v = string.IsNullOrWhiteSpace(value) ? Id : value.Trim();
            if (_settings.Name == v) return;
            _settings.Name = v;
            PersistSettings();
            OnChanged();
        }
    }

    /// Pin sticks the conversation to the top of its project's list,
    /// regardless of creation order. Toggled from the conversation row
    /// context menu / pin icon; persisted in settings so pins survive
    /// app restarts. Owning `ProjectViewModel` listens for this change
    /// and re-sorts its `Conversations` list.
    public bool IsPinned
    {
        get => _settings.IsPinned;
        set
        {
            if (_settings.IsPinned == value) return;
            _settings.IsPinned = value;
            PersistSettings();
            // ProjectViewModel observes this via PropertyChanged and calls
            // ResortConversations(), which is the only consumer — no need
            // for a separate PinChanged event.
            OnChanged();
        }
    }
    public void TogglePin() => IsPinned = !IsPinned;

    /// When true (default) the conversation uses the full Task Manager UI:
    /// plan, tasks, streaming output, and the Model/Effort/Ralph/KeepContext
    /// controls. When false, the UI collapses to a single full-window terminal
    /// that runs the selected tool's interactive yolo command (e.g.
    /// `claude --dangerously-skip-permissions`). Switching modes spawns/kills
    /// the per-conversation yolo terminal session.
    public bool IsTaskManagerEnabled
    {
        get => _settings.IsTaskManagerEnabled;
        set
        {
            if (_settings.IsTaskManagerEnabled == value) return;
            _settings.IsTaskManagerEnabled = value;
            PersistSettings();
            OnChanged();
            OnChanged(nameof(IsYoloModeEnabled));
            if (!value) EnsureYoloSession();
            else CloseYoloSession();
        }
    }

    public bool IsYoloModeEnabled => !IsTaskManagerEnabled;

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
            OnChangedMany(
                nameof(SelectedToolOption),
                nameof(ModelText),
                nameof(EffortText),
                nameof(ModelSuggestions),
                nameof(EffortSuggestions),
                nameof(IsPiTool));
            // In yolo mode the running CLI is the session itself — switching
            // tool means respawning so the new tool's interactive REPL takes
            // over the terminal.
            if (!IsTaskManagerEnabled) EnsureYoloSession();
        }
    }

    public IReadOnlyList<MainViewModel.ToolOption> ToolOptions => MainViewModel.AllToolOptions;

    public MainViewModel.ToolOption SelectedToolOption
    {
        get => MainViewModel.AllToolOptions.First(o => o.Tool == Tool);
        set { if (value != null) Tool = value.Tool; }
    }

    public IReadOnlyList<string> ModelSuggestions => Tool switch
    {
        CliTool.ClaudeCode => MainViewModel.ClaudeModels,
        CliTool.Codex => MainViewModel.CodexModels,
        CliTool.Pi => MainViewModel.PiModelsSeed,
        _ => Array.Empty<string>(),
    };
    public IReadOnlyList<string> EffortSuggestions => Tool switch
    {
        CliTool.ClaudeCode => MainViewModel.ClaudeEfforts,
        CliTool.Codex => MainViewModel.CodexEfforts,
        CliTool.Pi => MainViewModel.PiEfforts,
        _ => Array.Empty<string>(),
    };

    public string ModelText
    {
        get => (Tool switch
        {
            CliTool.ClaudeCode => _settings.ClaudeModel,
            CliTool.Codex => _settings.CodexModel,
            CliTool.Pi => _settings.PiModel,
            _ => null,
        }) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            switch (Tool)
            {
                case CliTool.ClaudeCode: _settings.ClaudeModel = v; break;
                case CliTool.Codex: _settings.CodexModel = v; break;
                case CliTool.Pi: _settings.PiModel = v; break;
            }
            PersistSettings();
            OnChanged();
        }
    }

    public string EffortText
    {
        get => (Tool switch
        {
            CliTool.ClaudeCode => _settings.ClaudeEffort,
            CliTool.Codex => _settings.CodexEffort,
            CliTool.Pi => _settings.PiThinking,
            _ => null,
        }) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            switch (Tool)
            {
                case CliTool.ClaudeCode: _settings.ClaudeEffort = v; break;
                case CliTool.Codex: _settings.CodexEffort = v; break;
                case CliTool.Pi: _settings.PiThinking = v; break;
            }
            PersistSettings();
            OnChanged();
        }
    }

    public bool IsPiTool => Tool == CliTool.Pi;

    /// Exposes the backing settings for read-only use by adjacent view models
    /// (e.g. GitViewModel uses this to pick the active tool + model when
    /// asking the AI for a commit message). Not for mutation — those go
    /// through the dedicated property setters above.
    public ConversationSettings SettingsSnapshot => _settings;

    // ---- Pi skills (per-conversation toggle set) ----
    public ObservableCollection<SkillPick> Skills { get; } = new();

    public int EnabledSkillCount => Skills.Count(s => s.IsEnabled);

    public string SkillsSummary
    {
        get
        {
            var n = EnabledSkillCount;
            if (Skills.Count == 0) return "No skills found";
            return n == 0 ? "No skills enabled" : $"{n} skill{(n == 1 ? "" : "s")} enabled";
        }
    }

    public void RefreshSkills()
    {
        var discovered = SkillsService.Discover(_workingDirectory);
        Skills.Clear();
        foreach (var s in discovered)
        {
            var pick = new SkillPick(s, _settings.EnabledSkills.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
            pick.Toggled += (_, _) =>
            {
                _settings.EnabledSkills = Skills.Where(x => x.IsEnabled).Select(x => x.Entry.Name).ToList();
                PersistSettings();
                OnChanged(nameof(EnabledSkillCount));
                OnChanged(nameof(SkillsSummary));
            };
            Skills.Add(pick);
        }
        OnChanged(nameof(EnabledSkillCount));
        OnChanged(nameof(SkillsSummary));
    }

    public IReadOnlyList<string> GetEnabledSkillPaths()
    {
        var discovered = SkillsService.Discover(_workingDirectory);
        var enabled = new HashSet<string>(_settings.EnabledSkills, StringComparer.OrdinalIgnoreCase);
        return discovered.Where(s => enabled.Contains(s.Name)).Select(s => s.Path).ToList();
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
            OnChanged(nameof(PrimaryActionText));
            OnChanged(nameof(IsPrimaryActionStart));
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
            if (!_suppressPromptSave)
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
            QueueTaskProjectionRefresh();
            if (!_suppressTasksSave)
            {
                _tasksSaveDebounce.Stop();
                _tasksSaveDebounce.Start();
            }
        }
    }

    private TaskViewFilter _taskPreviewFilter = TaskViewFilter.All;
    public string TaskPreviewMarkdown
    {
        get => _taskPreviewMarkdown;
        private set
        {
            if (_taskPreviewMarkdown == value) return;
            _taskPreviewMarkdown = value;
            OnChanged();
        }
    }
    private string _taskPreviewMarkdown = "";

    private int _summaryCount;
    public int SummaryCount
    {
        get => _summaryCount;
        private set { if (_summaryCount != value) { _summaryCount = value; OnChanged(); } }
    }

    public bool ShowAllTaskPreview
    {
        get => _taskPreviewFilter == TaskViewFilter.All;
        set { if (value) SetTaskPreviewFilter(TaskViewFilter.All); }
    }

    public bool ShowOpenTaskPreview
    {
        get => _taskPreviewFilter == TaskViewFilter.Open;
        set { if (value) SetTaskPreviewFilter(TaskViewFilter.Open); }
    }

    public bool ShowDoneTaskPreview
    {
        get => _taskPreviewFilter == TaskViewFilter.Done;
        set { if (value) SetTaskPreviewFilter(TaskViewFilter.Done); }
    }

    public bool ShowSummaryTaskPreview
    {
        get => _taskPreviewFilter == TaskViewFilter.Summaries;
        set { if (value) SetTaskPreviewFilter(TaskViewFilter.Summaries); }
    }

    public bool ShowLatestSummaryTaskPreview
    {
        get => _taskPreviewFilter == TaskViewFilter.LatestSummary;
        set { if (value) SetTaskPreviewFilter(TaskViewFilter.LatestSummary); }
    }

    public string TaskPreviewFilterLabel => _taskPreviewFilter switch
    {
        TaskViewFilter.Open => "Open",
        TaskViewFilter.Done => "Done",
        TaskViewFilter.Summaries => "Summaries",
        TaskViewFilter.LatestSummary => "Latest summary",
        _ => "All",
    };

    private int _currentIteration;
    public int CurrentIteration
    {
        get => _currentIteration;
        private set { if (_currentIteration != value) { _currentIteration = value; OnChanged(); OnChanged(nameof(IterationLabel)); } }
    }

    public string IterationLabel =>
        $"Iteration: {CurrentIteration} / {(_settings.RalphEnabled ? _settings.MaxIterations : 1)}";
    // Cached "mm:ss" renderings driven off the 500 ms UI tick. The tick
    // polls twice per second but the rendered value only ticks once per
    // second — caching lets us skip the redundant PropertyChanged on the
    // unchanged half-step, saving a binding rebind.
    private string _iterElapsedCache = TimeFormat.Placeholder;
    private string _totalElapsedCache = TimeFormat.Placeholder;
    public string IterationElapsedText => _iterElapsedCache;
    public string TotalElapsedText => _totalElapsedCache;

    /// Refresh cached elapsed strings. Fires PropertyChanged only on actual
    /// change. Returns true if either value changed.
    private bool RefreshElapsedCaches()
    {
        bool any = false;
        var iter = TimeFormat.Elapsed(_iterStartUtc);
        if (iter != _iterElapsedCache) { _iterElapsedCache = iter; OnChanged(nameof(IterationElapsedText)); any = true; }
        var total = TimeFormat.Elapsed(_totalStartUtc);
        if (total != _totalElapsedCache) { _totalElapsedCache = total; OnChanged(nameof(TotalElapsedText)); any = true; }
        return any;
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
        // Sidebar busy dot lights up for either the LoopRunner-driven task
        // manager mode OR the debounced output-activity signal from the yolo
        // terminal — both represent "this conversation is doing something".
        get => _isRunning || _yoloBusy;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnChanged();
            OnChanged(nameof(StartStopText));
            OnChanged(nameof(PrimaryActionText));
            OnChanged(nameof(IsPrimaryActionStart));
        }
    }

    public string StartStopText => IsRunning ? "Stop" : "Start";

    /// Label for the merged chat-bar action button. Says "Start" only when
    /// idle with an empty queue and Ralph mode on (the loop has nothing to
    /// send so the button kicks off the loop). Otherwise "Send" — either to
    /// queue a chat-only run or to inject mid-run.
    public string PrimaryActionText => IsPrimaryActionStart ? "Start" : "Send";

    public bool IsPrimaryActionStart =>
        !IsRunning && QueuedMessages.Count == 0 && RalphEnabled
        && string.IsNullOrWhiteSpace(_chatInput);

    /// Sessions older than this are treated as too stale to resume cleanly.
    private static readonly TimeSpan SessionTTL = TimeSpan.FromHours(24);

    /// True when `stamp` is older than <see cref="SessionTTL"/> or `null`
    /// (no stamp means we can't prove the session is fresh, so treat it
    /// as expired — the worst case is starting a new session).
    private static bool IsSessionStampExpired(DateTime? stamp) =>
        stamp is not DateTime ts || DateTime.UtcNow - ts > SessionTTL;

    private string? _currentSessionId;
    public string? CurrentSessionId
    {
        get => _currentSessionId;
        private set
        {
            if (_currentSessionId == value) return;
            _currentSessionId = value;
            _settings.LastSessionId = value;
            _settings.LastSessionTimestamp = value is null ? null : DateTime.UtcNow;
            PersistSettings();
            OnChanged();
            OnChanged(nameof(HasSession));
            OnChanged(nameof(SessionShort));
        }
    }

    public bool HasSession => !string.IsNullOrEmpty(_currentSessionId);

    /// Adopt an externally-issued session id (e.g. one captured from a
    /// `claude` run outside the app) so the next Start/Send resumes that
    /// conversation instead of opening a fresh one. The CLI receives this
    /// id verbatim via `--resume`; we also stamp the timestamp so the
    /// SessionTTL check on next load doesn't immediately expire it.
    public void ImportSessionId(string sessionId)
    {
        var trimmed = (sessionId ?? "").Trim();
        if (trimmed.Length == 0) return;
        CurrentSessionId = trimmed;
    }

    // ---- clipboard-copy feedback for the session pill ----
    private bool _isSessionCopied;
    public bool IsSessionCopied
    {
        get => _isSessionCopied;
        private set { if (_isSessionCopied != value) { _isSessionCopied = value; OnChanged(); } }
    }
    private DispatcherTimer? _copiedResetTimer;
    /// Flag the session pill as "just copied" for a brief visual pulse.
    /// The UI binds Text + Foreground to IsSessionCopied and flips back
    /// after ~1.2 s so the flash feels snappy but not subliminal.
    public void FlashSessionCopied()
    {
        IsSessionCopied = true;
        _copiedResetTimer?.Stop();
        _copiedResetTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _copiedResetTimer.Tick -= OnCopiedResetTick;
        _copiedResetTimer.Tick += OnCopiedResetTick;
        _copiedResetTimer.Start();
    }
    private void OnCopiedResetTick(object? sender, EventArgs e)
    {
        _copiedResetTimer?.Stop();
        IsSessionCopied = false;
    }

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
    public long InputTokens { get => _inputTokens; private set { if (_inputTokens != value) { _inputTokens = value; OnChanged(); InvalidateTokenSummary(); } } }
    public long OutputTokens { get => _outputTokens; private set { if (_outputTokens != value) { _outputTokens = value; OnChanged(); InvalidateTokenSummary(); } } }
    public long CachedTokens { get => _cachedTokens; private set { if (_cachedTokens != value) { _cachedTokens = value; OnChanged(); InvalidateTokenSummary(); } } }

    private int _toolCallCount;
    public int ToolCallCount
    {
        get => _toolCallCount;
        private set { if (_toolCallCount != value) { _toolCallCount = value; OnChanged(); } }
    }

    private int _allConsoleLineCount;
    public int AllConsoleLineCount
    {
        get => _allConsoleLineCount;
        private set { if (_allConsoleLineCount != value) { _allConsoleLineCount = value; OnChanged(); } }
    }

    private int _conversationConsoleLineCount;
    public int ConversationConsoleLineCount
    {
        get => _conversationConsoleLineCount;
        private set { if (_conversationConsoleLineCount != value) { _conversationConsoleLineCount = value; OnChanged(); } }
    }

    private int _toolConsoleLineCount;
    public int ToolConsoleLineCount
    {
        get => _toolConsoleLineCount;
        private set { if (_toolConsoleLineCount != value) { _toolConsoleLineCount = value; OnChanged(); } }
    }

    // ---- pinned-response surface ----
    private string _pinnedText = "";
    public string PinnedText
    {
        get => _pinnedText;
        private set
        {
            if (_pinnedText == value) return;
            _pinnedText = value;
            OnChanged();
            OnChanged(nameof(HasPinnedText));
        }
    }
    private bool _pinnedIsThinking;
    public bool PinnedIsThinking
    {
        get => _pinnedIsThinking;
        private set { if (_pinnedIsThinking != value) { _pinnedIsThinking = value; OnChanged(); OnChanged(nameof(PinnedLabel)); } }
    }
    public bool HasPinnedText => !string.IsNullOrWhiteSpace(_pinnedText);
    public string PinnedLabel => _pinnedIsThinking ? "LAST THINKING" : "LAST RESPONSE";

    // Throttle pinned-text updates so the UI isn't flooded during fast streams.
    private string? _pendingPinnedText;
    private bool? _pendingPinnedIsThinking;
    private DispatcherTimer? _pinFlushTimer;

    // ---- loop-health signals (pills in the status bar) ----

    private CircuitState _circuitState;
    public CircuitState CircuitState
    {
        get => _circuitState;
        private set
        {
            if (_circuitState == value) return;
            _circuitState = value;
            OnChanged();
            OnChanged(nameof(CircuitStateLabel));
            OnChanged(nameof(IsCircuitHalfOpen));
            OnChanged(nameof(IsCircuitOpen));
        }
    }
    public string CircuitStateLabel => _circuitState switch
    {
        CircuitState.HalfOpen => "Progress stalled",
        CircuitState.Open => "Circuit open",
        _ => "",
    };
    public bool IsCircuitHalfOpen => _circuitState == CircuitState.HalfOpen;
    public bool IsCircuitOpen => _circuitState == CircuitState.Open;

    private string? _exitStatus;
    public string? ExitStatus
    {
        get => _exitStatus;
        private set
        {
            if (_exitStatus == value) return;
            _exitStatus = value;
            OnChanged();
            OnChanged(nameof(ExitStatusVisible));
        }
    }
    public bool ExitStatusVisible => !string.IsNullOrEmpty(_exitStatus);

    private int _openTasks;
    public int OpenTasks
    {
        get => _openTasks;
        private set { if (_openTasks != value) { _openTasks = value; OnChanged(); OnChanged(nameof(TaskStatsVisible)); OnChanged(nameof(TaskProgressPercent)); OnChanged(nameof(TaskTotal)); } }
    }
    private int _closedTasks;
    public int ClosedTasks
    {
        get => _closedTasks;
        private set { if (_closedTasks != value) { _closedTasks = value; OnChanged(); OnChanged(nameof(TaskStatsVisible)); OnChanged(nameof(TaskProgressPercent)); OnChanged(nameof(TaskTotal)); } }
    }
    public bool TaskStatsVisible => _openTasks + _closedTasks > 0;
    public int TaskTotal => _openTasks + _closedTasks;
    /// 0-100; used by the status bar mini progress bar. Returns 0 when no tasks
    /// exist so the bar stays empty (the pill itself is hidden in that case).
    public double TaskProgressPercent
    {
        get
        {
            var total = _openTasks + _closedTasks;
            return total == 0 ? 0 : Math.Round(100.0 * _closedTasks / total, 1);
        }
    }

    public ObservableCollection<QueuedChatMessage> QueuedMessages { get; } = new();

    public bool HasQueuedMessages => QueuedMessages.Count > 0;
    public int QueuedCount => QueuedMessages.Count;
    /// "Clear all" is only offered when there are ≥2 items — for a single
    /// chip, the per-chip ✕ button is already right next to the text.
    public bool CanClearQueue => QueuedMessages.Count >= 2;

    /// Subtle "N queued · ~M tok" caption above the chips. Dirty-flagged so
    /// multiple binding reads per change (TextBlock.Text + Visibility trigger)
    /// don't re-walk the queue on each read. `InvalidateQueueHeader()` marks
    /// it dirty; the next get recomputes and stashes the result. Walks are
    /// O(N) — N is small but skipping them is still cheaper than walking.
    private DirtyMemo<string> _queueHeaderLabel = DirtyMemo<string>.Empty();
    private void InvalidateQueueHeader() => _queueHeaderLabel.Invalidate();
    public string QueueHeaderLabel => _queueHeaderLabel.Read(ComputeQueueHeaderLabel);

    private string ComputeQueueHeaderLabel()
    {
        int count = QueuedMessages.Count;
        if (count == 0) return "";
        int chars = 0;
        for (int i = 0; i < count; i++) chars += QueuedMessages[i].Text?.Length ?? 0;
        var countPart = count == 1 ? "1 queued" : $"{count} queued";
        var tokenPart = TextStats.FormatTokens(TextStats.ApproxTokens(chars));
        return tokenPart.Length > 0 ? $"{countPart} · {tokenPart}" : countPart;
    }

    /// -1 when the chat box holds a new draft; 0..Count-1 when navigating
    /// previously-queued items via Up/Down for editing. Edits to `ChatInput`
    /// while in recall mode are written through to the focused queue entry.
    private int _chatRecallIndex = -1;
    public int ChatRecallIndex
    {
        get => _chatRecallIndex;
        private set
        {
            if (_chatRecallIndex == value) return;
            _chatRecallIndex = value;
            OnChanged();
            OnChanged(nameof(IsRecallingQueued));
            RefreshQueueActiveFlags();
        }
    }

    /// Re-evaluate each chip's `IsActive` and `IsHead` flags against the
    /// current recall index and collection position. O(N) — N is always small.
    /// Short-circuits when the recall index hasn't moved AND the collection
    /// count matches what we last saw, so redundant fires are cheap.
    private int _lastRefreshedActiveIndex = int.MinValue;
    private int _lastRefreshedCount = -1;
    private string? _lastRefreshedHeadId;
    private void RefreshQueueActiveFlags()
    {
        int active = _chatRecallIndex;
        int count = QueuedMessages.Count;
        // Include head chip identity in the short-circuit key. Count-and-index
        // alone miss Move events that shuffle the order without changing the
        // count — the previous head would otherwise stay marked IsHead=true
        // even after the user reorders chips with the new up/down buttons.
        string? headId = count > 0 ? QueuedMessages[0].Id : null;
        if (active == _lastRefreshedActiveIndex
            && count == _lastRefreshedCount
            && ReferenceEquals(headId, _lastRefreshedHeadId)) return;
        _lastRefreshedActiveIndex = active;
        _lastRefreshedCount = count;
        _lastRefreshedHeadId = headId;
        // Head pill only renders when there are ≥2 chips — with one chip
        // the dequeue order is already obvious.
        bool showHead = count >= 2;
        int lastIdx = count - 1;
        for (int i = 0; i < count; i++)
        {
            var m = QueuedMessages[i];
            bool shouldBeActive = (i == active);
            bool shouldBeHead = showHead && i == 0;
            if (m.IsActive != shouldBeActive) m.IsActive = shouldBeActive;
            if (m.IsHead != shouldBeHead) m.IsHead = shouldBeHead;
            if (m.IsFirst != (i == 0)) m.IsFirst = (i == 0);
            if (m.IsLast != (i == lastIdx)) m.IsLast = (i == lastIdx);
        }
    }

    private void QueuedMessage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuedChatMessage.Text))
        {
            InvalidateQueueHeader();
            OnChanged(nameof(QueueHeaderLabel));
        }
    }
    public bool IsRecallingQueued => _chatRecallIndex >= 0;

    /// Draft text stashed the moment the user enters recall mode, so we can
    /// restore it when they navigate past the newest queued item.
    private string _chatDraftSaved = "";
    private bool _suppressRecallSync;

    private string _chatInput = "";
    private bool _queuedInjectionRequested;
    public string ChatInput
    {
        get => _chatInput;
        set
        {
            if (_chatInput == value) return;
            _chatInput = value;
            // Live-sync edits through to the focused queued item so that
            // Up/Down navigation doesn't lose the user's changes.
            if (!_suppressRecallSync &&
                _chatRecallIndex >= 0 && _chatRecallIndex < QueuedMessages.Count)
            {
                QueuedMessages[_chatRecallIndex].Text = value;
            }
            OnChanged();
            OnChangedMany(
                nameof(ChatInputStatsLabel),
                nameof(HasChatInputStats),
                nameof(PrimaryActionText),
                nameof(IsPrimaryActionStart));
        }
    }

    /// Subtle word/char readout rendered above the chat input. Empty when
    /// the box is empty so the UI stays quiet until the user has typed.
    /// Cached because WPF may read the binding multiple times between
    /// keystrokes (once for the TextBlock, once for the Visibility trigger).
    private string _chatInputStatsCache = "";
    private string _chatInputStatsSource = "";
    public string ChatInputStatsLabel
    {
        get
        {
            var src = _chatInput ?? "";
            if (!ReferenceEquals(src, _chatInputStatsSource) && src != _chatInputStatsSource)
            {
                _chatInputStatsSource = src;
                _chatInputStatsCache = TextStats.FormatLabel(src);
            }
            return _chatInputStatsCache;
        }
    }
    public bool HasChatInputStats => !string.IsNullOrEmpty(_chatInput);

    public void EnqueueChat()
    {
        // Enter while recalling = "done editing this queued item". The edits
        // have already been live-synced; just exit recall and restore the draft.
        if (_chatRecallIndex >= 0)
        {
            ExitRecallMode();
            return;
        }
        var t = (_chatInput ?? "").Trim();
        if (t.Length == 0)
        {
            if (IsRunning && QueuedMessages.Count > 0)
                _ = ForceInjectQueuedAsync();
            return;
        }
        QueuedMessages.Add(new QueuedChatMessage(t));
        ChatInput = "";
        TryStartAutoNaming(t);
        if (!IsRunning) _ = StartRunAsync(chatOnly: true);
    }

    // Fires once per conversation: the first user message we see while the
    // title is still the placeholder ("Conversation N" / id) gets handed to
    // the internal OpenRouter namer to invent a 3-6 word title. Background task,
    // never blocks the chat send; result is dispatched onto the UI thread.
    private bool _autoNameInFlight;
    private void TryStartAutoNaming(string promptText)
    {
        if (_isDisposed) return;
        if (_autoNameInFlight) return;
        if (!ConversationNamer.IsDefaultName(Name, Id)) return;

        var appSettings = _appSettingsProvider();
        var apiKey = appSettings.OpenRouterApiKey;
        var model = appSettings.OpenRouterTitleModel;
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        _autoNameInFlight = true;

        _ = Task.Run(async () =>
        {
            string? title = null;
            try { title = await ConversationNamer.GenerateTitleAsync(apiKey, model, promptText); }
            catch { /* network / spawn failures shouldn't surface */ }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _autoNameInFlight = false;
                return;
            }
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Re-check on the UI thread: the user may have typed their
                    // own title in the meantime, in which case we don't clobber it.
                    if (!string.IsNullOrWhiteSpace(title)
                        && !_isDisposed
                        && ConversationNamer.IsDefaultName(Name, Id))
                        Name = title;
                }
                finally
                {
                    _autoNameInFlight = false;
                }
            });
        });
    }

    public void RemoveQueued(QueuedChatMessage msg)
    {
        if (msg == null) return;
        int idx = QueuedMessages.IndexOf(msg);
        if (idx < 0) return;
        QueuedMessages.RemoveAt(idx);
        if (_chatRecallIndex == idx) ExitRecallMode();
        else if (_chatRecallIndex > idx) ChatRecallIndex = _chatRecallIndex - 1;
    }

    /// Load a queued message into the chat input for in-place editing.
    /// Mirrors what Up/Down recall does but lets the UI trigger it by click.
    /// No-op if the message is no longer in the queue (it may have been
    /// dequeued by the loop runner between the mouse-down and this call).
    public void BeginEditQueued(QueuedChatMessage msg)
    {
        if (msg == null) return;
        int idx = QueuedMessages.IndexOf(msg);
        if (idx < 0) return;
        if (_chatRecallIndex < 0) _chatDraftSaved = _chatInput ?? "";
        ChatRecallIndex = idx;
        SetChatInputFromRecall();
    }

    /// Clone an existing queued message and insert the copy immediately after
    /// it. Handy for small variations ("the same thing but for file X"). The
    /// new chip becomes the active recall target so the user can edit in place.
    public void DuplicateQueued(QueuedChatMessage msg)
    {
        if (msg == null) return;
        int idx = QueuedMessages.IndexOf(msg);
        if (idx < 0) return;
        var copy = new QueuedChatMessage(msg.Text ?? "");
        QueuedMessages.Insert(idx + 1, copy);
        // Push recall cursor past the duplicate so live-edits in the chat box
        // don't accidentally re-write the original.
        if (_chatRecallIndex == idx) ChatRecallIndex = idx + 1;
    }

    /// Shift a queued chip one slot earlier in the dequeue order. The chip at
    /// index 0 is the next to run, so "up" means "closer to the front". Keeps
    /// the recall cursor pinned to the same logical chip so in-progress edits
    /// don't jump to a different message.
    public void MoveQueuedUp(QueuedChatMessage msg) => MoveQueued(msg, -1);

    /// Shift a queued chip one slot later in the dequeue order. No-op when
    /// the chip is already at the tail.
    public void MoveQueuedDown(QueuedChatMessage msg) => MoveQueued(msg, +1);

    /// Reorder whichever chip the recall cursor is pointing at. Keyboard
    /// shortcut entry point (Alt+Up / Alt+Down from the chat input) — lets
    /// the user reorder a chip they're currently editing without taking
    /// their hands off the keyboard. Returns true when something moved so
    /// the key handler can e.Handled=true only on success.
    public bool MoveRecalledQueued(int delta)
    {
        if (_chatRecallIndex < 0) return false;
        if ((uint)_chatRecallIndex >= (uint)QueuedMessages.Count) return false;
        int before = _chatRecallIndex;
        MoveQueued(QueuedMessages[_chatRecallIndex], delta);
        return _chatRecallIndex != before;
    }

    private void MoveQueued(QueuedChatMessage msg, int delta)
    {
        if (msg == null) return;
        int idx = QueuedMessages.IndexOf(msg);
        if (idx < 0) return;
        int target = idx + delta;
        if ((uint)target >= (uint)QueuedMessages.Count) return;
        QueuedMessages.Move(idx, target);
        // The recall cursor follows whichever chip physically moved into the
        // slot it was watching — that's `idx` for the displaced chip, `target`
        // for the moved chip. Any other index is unaffected.
        if (_chatRecallIndex == idx) ChatRecallIndex = target;
        else if (_chatRecallIndex == target) ChatRecallIndex = idx;
    }

    /// Drop every queued message in one shot. Fires CollectionChanged=Reset
    /// exactly once (ObservableCollection.Clear semantics), so the UI does a
    /// single rebind rather than N per-item removals.
    public void ClearQueue()
    {
        if (QueuedMessages.Count == 0) return;
        if (_chatRecallIndex >= 0) ExitRecallMode();
        QueuedMessages.Clear();
    }

    /// Step the recall cursor toward older queued items. Entering recall
    /// mode for the first time stashes the current draft.
    public void RecallQueuedPrev()
    {
        if (QueuedMessages.Count == 0) return;
        if (_chatRecallIndex < 0)
        {
            _chatDraftSaved = _chatInput ?? "";
            ChatRecallIndex = QueuedMessages.Count - 1;
        }
        else if (_chatRecallIndex > 0)
        {
            ChatRecallIndex = _chatRecallIndex - 1;
        }
        else return;
        SetChatInputFromRecall();
    }

    /// Step the recall cursor toward newer queued items. Going past the most
    /// recent exits recall and restores the saved draft.
    public void RecallQueuedNext()
    {
        if (_chatRecallIndex < 0) return;
        if (_chatRecallIndex < QueuedMessages.Count - 1)
        {
            ChatRecallIndex = _chatRecallIndex + 1;
            SetChatInputFromRecall();
        }
        else
        {
            ExitRecallMode();
        }
    }

    public void ExitRecallMode()
    {
        if (_chatRecallIndex < 0) return;
        int idx = _chatRecallIndex;
        ChatRecallIndex = -1;
        SetChatInputText(_chatDraftSaved);
        _chatDraftSaved = "";
        // If the user blanked the item while editing, drop it so we never
        // dequeue an empty message and echo an empty banner.
        if (idx >= 0 && idx < QueuedMessages.Count &&
            string.IsNullOrWhiteSpace(QueuedMessages[idx].Text))
        {
            QueuedMessages.RemoveAt(idx);
        }
    }

    private void SetChatInputFromRecall()
    {
        if (_chatRecallIndex < 0 || _chatRecallIndex >= QueuedMessages.Count) return;
        SetChatInputText(QueuedMessages[_chatRecallIndex].Text);
    }

    private void SetChatInputText(string value)
    {
        _suppressRecallSync = true;
        try { ChatInput = value; } finally { _suppressRecallSync = false; }
    }

    /// Called from LoopRunner between iterations (on UI thread). Removes and
    /// returns the head of the queue, or null if empty. Adjusts the recall
    /// cursor so it never points at a dequeued item. Skips blank items so
    /// that a stray empty queue entry can never surface as a silent submit.
    ///
    /// Also recognises the `/plan` slash command and rewraps it with strict
    /// plan-file-editing instructions so the model updates the plan file
    /// instead of executing work.
    private string? TryDequeueQueued()
    {
        while (QueuedMessages.Count > 0)
        {
            var head = QueuedMessages[0];
            QueuedMessages.RemoveAt(0);
            if (_chatRecallIndex == 0) ExitRecallMode();
            else if (_chatRecallIndex > 0) ChatRecallIndex = _chatRecallIndex - 1;
            if (string.IsNullOrWhiteSpace(head.Text)) continue;

            var expanded = ExpandPromptForSubmission(head.Text);
            if (TryParsePlanCommand(expanded, out var planBody))
                return BuildPlanModePrompt(planBody);
            return expanded;
        }
        return null;
    }

    private static bool TryParsePlanCommand(string text, out string body)
    {
        body = "";
        if (string.IsNullOrEmpty(text)) return false;
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("/plan", StringComparison.OrdinalIgnoreCase)) return false;
        // Ensure it's a command boundary — not "/planet" or similar.
        if (trimmed.Length > 5 && !char.IsWhiteSpace(trimmed[5])) return false;
        body = trimmed.Length > 5 ? trimmed.Substring(6).TrimStart() : "";
        return true;
    }

    private string BuildPlanModePrompt(string userIntent)
    {
        var planPath = PromptFile.Replace('\\', '/');
        var intent = string.IsNullOrWhiteSpace(userIntent)
            ? "(no explicit intent — review the existing plan and propose improvements)"
            : userIntent;
        return $@"[PLAN MODE — STRICT]

You are updating the project plan file. You are NOT executing any of its tasks this turn.

Plan file (the JustCode ""PLAN"" pane): `{planPath}`

Do exactly this, in order:
1. READ `{planPath}` in full using the Read tool.
2. UNDERSTAND the update intent below.
3. EDIT `{planPath}` in place using the Edit tool. Preserve existing sections, wording, and ordering wherever possible; only change what is necessary. If the plan is empty, write a clear structured first version with short sections (Goal, Constraints, Next Steps).
4. After the edit lands, respond in chat with a short bullet list of the specific changes you made (added / removed / reworded).

Hard rules for this turn:
- Do NOT execute any of the plan's tasks.
- Do NOT edit any file other than `{planPath}`.
- Do NOT run build/test/lint commands.
- Do NOT append a `session summary` heading — this is planning, not a work session.

--- UPDATE INTENT ---
{intent}
";
    }

    // In-progress iteration estimates (reset when actual usage arrives).
    private long _estInputChars;
    private long _estOutputChars;
    private DateTime _lastTokenNotify = DateTime.MinValue;
    private readonly DispatcherTimer _taskPreviewDebounce;
    private string _lastProjectedTasksSource = "";
    private TaskViewFilter _lastProjectedFilter = TaskViewFilter.All;
    private bool _hasProjectedTasks;

    // Cached so the binding (read by Text + Visibility triggers) doesn't
    // rebuild a 3-segment string on every read. Dirty-flag invalidation
    // via DirtyMemo; callers invoke InvalidateTokenSummary() whenever any
    // of the four inputs changes (token counters or char-estimate sinks).
    private DirtyMemo<string> _tokenSummary = DirtyMemo<string>.Empty();
    private void InvalidateTokenSummary()
    {
        _tokenSummary.Invalidate();
        OnChanged(nameof(TokenSummary));
    }
    public string TokenSummary => _tokenSummary.Read(ComputeTokenSummary);
    private string ComputeTokenSummary()
    {
        long inDisplay = _inputTokens + TextStats.ApproxTokens(_estInputChars);
        long outDisplay = _outputTokens + TextStats.ApproxTokens(_estOutputChars);
        bool hasEst = _estInputChars > 0 || _estOutputChars > 0;
        if (inDisplay == 0 && outDisplay == 0) return "";
        var s = $"{inDisplay:N0} in · {outDisplay:N0} out";
        if (_cachedTokens > 0) s += $" · {_cachedTokens:N0} cached";
        if (hasEst) s += " · est";
        return s;
    }

    public ConversationViewModel(
        string workingDirectory,
        ConversationSettings settings,
        Action persistSettings,
        Func<LoopSettings>? appSettingsProvider = null)
    {
        _workingDirectory = workingDirectory;
        _settings = settings;
        _persistSettings = persistSettings;
        _appSettingsProvider = appSettingsProvider ?? (() => new LoopSettings());
        QueuedMessages.CollectionChanged += (_, e) =>
        {
            // Swap per-item text subscriptions so the header's token total
            // reflects in-place edits (click-to-edit / recall typing), not
            // just add/remove/reset events.
            if (e.OldItems != null)
                foreach (QueuedChatMessage m in e.OldItems) m.PropertyChanged -= QueuedMessage_PropertyChanged;
            if (e.NewItems != null)
                foreach (QueuedChatMessage m in e.NewItems) m.PropertyChanged += QueuedMessage_PropertyChanged;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                foreach (var m in QueuedMessages) m.PropertyChanged += QueuedMessage_PropertyChanged;

            InvalidateQueueHeader();
            OnChangedMany(
                nameof(HasQueuedMessages),
                nameof(QueuedCount),
                nameof(CanClearQueue),
                nameof(QueueHeaderLabel),
                nameof(PrimaryActionText),
                nameof(IsPrimaryActionStart));
            RefreshQueueActiveFlags();
        };
        // Expire stale session ids on load so we never try to resume into a
        // context the model has likely forgotten.
        if (!string.IsNullOrEmpty(settings.LastSessionId)
            && IsSessionStampExpired(settings.LastSessionTimestamp))
        {
            settings.LastSessionId = null;
            settings.LastSessionTimestamp = null;
        }
        _currentSessionId = settings.LastSessionId;

        ConsoleParagraph = new Paragraph { Margin = new Thickness(0), TextIndent = 0 };
        ConsoleDocument = new FlowDocument(ConsoleParagraph)
        {
            PageWidth = 6000,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        ConversationConsoleParagraph = new Paragraph { Margin = new Thickness(0), TextIndent = 0 };
        ConversationConsoleDocument = new FlowDocument(ConversationConsoleParagraph)
        {
            PageWidth = 6000,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        ToolConsoleParagraph = new Paragraph { Margin = new Thickness(0), TextIndent = 0 };
        ToolConsoleDocument = new FlowDocument(ToolConsoleParagraph)
        {
            PageWidth = 6000,
            Background = System.Windows.Media.Brushes.Transparent,
        };

        var convDir = ConversationStore.ConversationDir(workingDirectory, settings.Id);
        Directory.CreateDirectory(convDir);
        _promptStore = new PromptStore(convDir, PromptFile);
        _tasksFile = new TasksFileService();
        _consoleLog = new ConsoleLogStore(Path.Combine(convDir, "console.log"));
        var history = _consoleLog.Load();
        _pendingConsoleHistory = string.IsNullOrEmpty(history) ? null : history;

        _tasksSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tasksSaveDebounce.Tick += (_, _) =>
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        };
        _taskPreviewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _taskPreviewDebounce.Tick += (_, _) =>
        {
            _taskPreviewDebounce.Stop();
            RefreshTaskProjectionNow();
        };

        _tasksFile.ExternalChange += (_, text) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyExternalTasks(text));

        _uiTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTick.Tick += (_, _) => RefreshElapsedCaches();

        _loopRunner = new LoopRunner(new CliProcessRunner());
        _loopRunner.Output += (_, chunk) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _consoleLog.Append(chunk);
                ConsoleAppend?.Invoke(this, chunk);
            });
        _loopRunner.Status += (_, s) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Status = s;
            IsRunning = s is "Running" or "Restarting" or "Killing";
            if (!IsRunning)
            {
                _uiTick.Stop();
                RefreshElapsedCaches();
            }
        });
        _loopRunner.IterationChanged += (_, t) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CurrentIteration = t.current;
            _iterStartUtc = DateTime.UtcNow;
            RefreshElapsedCaches();
        });
        _loopRunner.PromptInjected += (_, p) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _promptAtLastInjection = p;
            PromptPendingChange = false;
        });
        _loopRunner.SessionCaptured += (_, sid) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // An empty payload means "reset" — LoopRunner fires this when it
            // auto-recovers from a fatal error by starting a fresh session.
            // Null out CurrentSessionId (which persists LastSessionId=null) so
            // the next run captures a genuinely new id.
            CurrentSessionId = string.IsNullOrEmpty(sid) ? null : sid;
            // Fork is one-shot: once a fresh session id lands, the pending-fork
            // pointer has done its job. Clear it so subsequent iterations
            // behave like a normal resume.
            if (!string.IsNullOrEmpty(_settings.PendingForkFromSessionId))
            {
                _settings.PendingForkFromSessionId = null;
                PersistSettings();
            }
        });
        _loopRunner.ToolCallInvoked += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ToolCallCount++;
        });
        // Pinned response: staged via a 120ms flush timer so fast token streams
        // don't hammer the UI thread. Each event stages the latest full block
        // text and kind; the timer commits them together.
        _pinFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _pinFlushTimer.Tick += (_, _) =>
        {
            _pinFlushTimer!.Stop();
            if (_pendingPinnedText is null) return;
            PinnedIsThinking = _pendingPinnedIsThinking ?? false;
            PinnedText = _pendingPinnedText;
            _pendingPinnedText = null;
            _pendingPinnedIsThinking = null;
        };
        _loopRunner.PinnedResponseUpdated += (_, p) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _pendingPinnedText = p.text;
            _pendingPinnedIsThinking = p.isThinking;
            if (!_pinFlushTimer!.IsEnabled) _pinFlushTimer.Start();
        });
        _loopRunner.CircuitStateChanged += (_, s) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CircuitState = s;
        });
        _loopRunner.ExitSignalReceived += (_, status) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ExitStatus = status;
        });
        _loopRunner.TaskStatsUpdated += (_, t) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplyTaskCounts(t.open, t.closed);
        });
        _loopRunner.TokenUsageReported += (_, u) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Result events carry per-turn usage — accumulate across iterations
            // and reset the in-progress estimate for that iteration.
            InputTokens += u.input;
            OutputTokens += u.output;
            CachedTokens += u.cached;
            _estInputChars = 0;
            _estOutputChars = 0;
            InvalidateTokenSummary();
        });
        _loopRunner.EstimatedInputCharsSet += (_, n) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // New iteration started. Reset the in-progress estimates to just
            // this iteration's input payload.
            _estInputChars = n;
            _estOutputChars = 0;
            InvalidateTokenSummary();
        });
        _loopRunner.EstimatedOutputCharsAppended += (_, n) =>
        {
            // Background-thread event — dispatch and throttle UI updates.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _estOutputChars += n;
                var now = DateTime.UtcNow;
                if ((now - _lastTokenNotify).TotalMilliseconds >= 120)
                {
                    _lastTokenNotify = now;
                    InvalidateTokenSummary();
                }
            });
        };

        _prompt = _promptStore.LoadPrompt();
        _promptStore.ExternalChange += (_, text) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyExternalPrompt(text));
        _promptStore.Watch();
        _tasksFile.Watch(TasksFile);
        _tasksText = _tasksFile.Load();
        QueueTaskProjectionRefresh(immediate: true);
        // Skills discovery is deferred to first popup open. The in-process
        // cache in SkillsService makes repeat access cheap, so running it
        // N times (once per conversation) at project open was pure waste.
    }

    /// Called when the plan file changes on disk from outside this VM (e.g.
    /// the model edited it in /plan mode). Update the VM's view of the
    /// prompt without re-triggering a save round-trip.
    private bool _suppressPromptSave;
    private void ApplyExternalPrompt(string text)
    {
        if (_prompt == text) return;
        _suppressPromptSave = true;
        try
        {
            _prompt = text;
            OnChanged(nameof(Prompt));
        }
        finally { _suppressPromptSave = false; }
    }

    private void ApplyExternalTasks(string text)
    {
        if (_tasksText == text) return;
        _suppressTasksSave = true;
        try { TasksText = text; }
        finally { _suppressTasksSave = false; }
    }

    private void ApplyTaskCounts(int open, int closed)
    {
        OpenTasks = open;
        ClosedTasks = closed;
    }

    private void SetTaskPreviewFilter(TaskViewFilter filter)
    {
        if (_taskPreviewFilter == filter) return;
        _taskPreviewFilter = filter;
        OnChanged(nameof(ShowAllTaskPreview));
        OnChanged(nameof(ShowOpenTaskPreview));
        OnChanged(nameof(ShowDoneTaskPreview));
        OnChanged(nameof(ShowSummaryTaskPreview));
        OnChanged(nameof(ShowLatestSummaryTaskPreview));
        OnChanged(nameof(TaskPreviewFilterLabel));
        QueueTaskProjectionRefresh(immediate: true);
    }

    private void QueueTaskProjectionRefresh(bool immediate = false)
    {
        if (immediate)
        {
            _taskPreviewDebounce.Stop();
            RefreshTaskProjectionNow();
            return;
        }

        _taskPreviewDebounce.Stop();
        _taskPreviewDebounce.Start();
    }

    private void RefreshTaskProjectionNow()
    {
        if (_hasProjectedTasks &&
            _lastProjectedFilter == _taskPreviewFilter &&
            string.Equals(_lastProjectedTasksSource, _tasksText, StringComparison.Ordinal))
            return;

        var projection = TasksMarkdownAnalyzer.CreateProjection(_tasksText, _taskPreviewFilter);
        _hasProjectedTasks = true;
        _lastProjectedTasksSource = _tasksText;
        _lastProjectedFilter = _taskPreviewFilter;
        ApplyTaskCounts(projection.Stats.Open, projection.Stats.Closed);
        SummaryCount = projection.Stats.SummaryCount;
        TaskPreviewMarkdown = projection.Content;
    }

    public Task ToggleStartStopAsync()
    {
        if (IsRunning)
        {
            _loopRunner.Stop();
            return Task.CompletedTask;
        }
        return StartRunAsync(chatOnly: false);
    }

    /// User hit Send again with an empty chat box while messages are already
    /// queued and a turn is in flight. Treat that as "send now": kill the
    /// current iteration, then immediately resume the conversation with the
    /// queued messages as fresh turns. We force session continuation when we
    /// already own a captured session id so the queued text lands inside the
    /// same conversation even if Keep Context is off.
    private async Task ForceInjectQueuedAsync()
    {
        if (_queuedInjectionRequested || !IsRunning || QueuedMessages.Count == 0) return;
        _queuedInjectionRequested = true;
        try
        {
            _loopRunner.Stop();
            for (int i = 0; i < 120 && _loopRunner.IsRunning; i++)
                await Task.Delay(50);
            if (_loopRunner.IsRunning || QueuedMessages.Count == 0) return;
            await StartRunAsync(chatOnly: true, forceContinueSession: true);
        }
        finally
        {
            _queuedInjectionRequested = false;
        }
    }

    private async Task StartRunAsync(bool chatOnly, bool forceContinueSession = false)
    {
        if (_isDisposed) return;
        if (!chatOnly)
            TryStartAutoNaming(_prompt);
        _promptStore.FlushPrompt();
        _promptAtLastInjection = null;
        PromptPendingChange = false;
        InputTokens = 0;
        OutputTokens = 0;
        CachedTokens = 0;
        ToolCallCount = 0;
        CircuitState = CircuitState.Closed;
        ExitStatus = null;
        _estInputChars = 0;
        _estOutputChars = 0;
        InvalidateTokenSummary();
        IsRunning = true;
        Status = "Running";
        CurrentIteration = 0;
        _totalStartUtc = DateTime.UtcNow;
        _iterStartUtc = DateTime.UtcNow;
        _uiTick.Start();
        RefreshElapsedCaches();
        OnChanged(nameof(IterationLabel));
        try
        {
            var tasksRel = System.IO.Path.Combine(".looper", "conversations", Id, "tasks.md")
                .Replace('\\', '/');
            await _loopRunner.RunAsync(
                () => ExpandPromptForSubmission(_prompt),
                TryDequeueQueued,
                _settings, _workingDirectory, tasksRel,
                initialSessionId: _currentSessionId,
                chatOnly: chatOnly,
                enabledSkillPathsProvider: GetEnabledSkillPaths,
                forceContinueSession: forceContinueSession);
        }
        finally
        {
            IsRunning = false;
            _uiTick.Stop();
            _promptAtLastInjection = null;
            PromptPendingChange = false;
            RefreshElapsedCaches();
        }
    }

    public void StopIfRunning()
    {
        if (IsRunning) _loopRunner.Stop();
    }

    public void ClearSession()
    {
        CurrentSessionId = null;
        _consoleLog.Clear();
        ClearConsoleViews();
        PinnedText = "";
        PinnedIsThinking = false;
    }

    public void RecordConsoleLineCounts(int allDelta, int conversationDelta, int toolDelta)
    {
        if (allDelta != 0) AllConsoleLineCount += allDelta;
        if (conversationDelta != 0) ConversationConsoleLineCount += conversationDelta;
        if (toolDelta != 0) ToolConsoleLineCount += toolDelta;
    }

    public void ClearConsoleViews()
    {
        ConsoleParagraph.Inlines.Clear();
        ConversationConsoleParagraph.Inlines.Clear();
        ToolConsoleParagraph.Inlines.Clear();
        AllConsoleLineCount = 0;
        ConversationConsoleLineCount = 0;
        ToolConsoleLineCount = 0;
        ResetConversationLineState();
    }

    public string GetRecentYoloTerminalText(int maxChars)
    {
        if (maxChars <= 0) return "";
        var session = _yoloSession ?? _yoloPanel?.ActiveSession;
        if (session == null) return "";

        try
        {
            var raw = session.GetOutputHistorySnapshot();
            if (raw.Length == 0) return "";
            var stripped = TerminalSessionViewModel.StripAnsi(raw);
            var text = Encoding.UTF8.GetString(stripped);
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]+", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
            if (text.Length <= maxChars) return text;

            var tail = text.Substring(text.Length - maxChars);
            var firstNewline = tail.IndexOf('\n');
            return firstNewline >= 0 && firstNewline < tail.Length - 1
                ? tail.Substring(firstNewline + 1).Trim()
                : tail.Trim();
        }
        catch { return ""; }
    }

    // ---------- conversation-view line filter ----------
    // The conversation paragraph hides tool-use lines, which leaves the model's
    // own thinking/text blocks looking *adjacent* even when there was a tool
    // call between them. Without this filter, every thinking block re-prints
    // its "🧠 thinking…" header and each block contributes leading/trailing
    // newlines, so two thoughts separated by a hidden tool call render as a
    // tall pile of empty rows. We track the most-recently-appended kind and
    // buffer blank lines so we can collapse them and drop redundant headers.
    private bool _conversationLastWasThinking;
    private int _conversationBlankBuffer;

    public enum ConversationLineDecision { Skip, AppendBlankThenLine, AppendLineOnly }

    public void ResetConversationLineState()
    {
        _conversationLastWasThinking = false;
        _conversationBlankBuffer = 0;
    }

    public ConversationLineDecision RouteConversationLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            _conversationBlankBuffer++;
            return ConversationLineDecision.Skip;
        }
        var trimmed = line.Length > 0 && line[^1] == '\n'
            ? line.Substring(0, line.Length - 1)
            : line;
        bool isHeader = trimmed.StartsWith("🧠 thinking", StringComparison.Ordinal);
        bool isGutter = trimmed.StartsWith("│ ", StringComparison.Ordinal) || trimmed == "│";
        if (isHeader && _conversationLastWasThinking)
        {
            // Drop the duplicate header *and* any blank lines that would have
            // padded it — the prior thinking gutter line continues straight
            // into the next one with no visual seam.
            _conversationBlankBuffer = 0;
            return ConversationLineDecision.Skip;
        }
        bool needBlank = _conversationBlankBuffer > 0;
        _conversationBlankBuffer = 0;
        _conversationLastWasThinking = isHeader || isGutter;
        return needBlank
            ? ConversationLineDecision.AppendBlankThenLine
            : ConversationLineDecision.AppendLineOnly;
    }

    /// Called once by the view after it has attached to the ConsoleAppend
    /// stream. Returns the persisted console history (if any) so the view
    /// can replay it through its styling pipeline.
    public string? PopConsoleHistory()
    {
        var h = _pendingConsoleHistory;
        _pendingConsoleHistory = null;
        return h;
    }

    public void NotifyMaxIterChanged() => OnChanged(nameof(IterationLabel));

    // ---------- @-mention map (short label ↔ full relative path) ----------
    private static readonly System.Text.RegularExpressions.Regex MentionRefRegex = new(
        @"@(?:""([^""\r\n]+)""|([^\s""]+))",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public string RegisterMention(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
        var norm = fullPath.Replace('\\', '/').TrimStart('/').TrimEnd('/');

        foreach (var kv in _settings.MentionMap)
            if (string.Equals(kv.Value, norm, StringComparison.OrdinalIgnoreCase))
                return kv.Key;

        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int start = parts.Length - 1; start >= 0; start--)
        {
            var candidate = string.Join('/', parts.Skip(start));
            if (!_settings.MentionMap.TryGetValue(candidate, out var existing))
            {
                _settings.MentionMap[candidate] = norm;
                PersistSettings();
                return candidate;
            }
            if (string.Equals(existing, norm, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        _settings.MentionMap[norm] = norm;
        PersistSettings();
        return norm;
    }

    public string ExpandPromptForSubmission(string text)
    {
        if (string.IsNullOrEmpty(text) || _settings.MentionMap.Count == 0) return text;
        return MentionRefRegex.Replace(text, m =>
        {
            var label = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (_settings.MentionMap.TryGetValue(label, out var full))
                return "@" + (full.Any(c => c is ' ' or '\t') ? "\"" + full + "\"" : full);
            return m.Value;
        });
    }

    public bool TryGetMentionFullPath(string label, out string fullPath)
    {
        if (_settings.MentionMap.TryGetValue(label, out var v))
        {
            fullPath = v;
            return true;
        }
        fullPath = "";
        return false;
    }

    public void Shutdown()
    {
        if (_isDisposed) return;
        _loopRunner.Stop();
        _promptStore.FlushPrompt();
        FlushPersistSettings();
        if (_tasksSaveDebounce.IsEnabled)
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        }
        _taskPreviewDebounce.Stop();
        _tasksFile.Dispose();
        _promptStore.Dispose();
        _consoleLog.Dispose();
        _yoloBusyTimer?.Stop();
        _yoloSessionWatcher?.Dispose();
        _yoloSessionWatcher = null;
        try { _yoloPanel?.Dispose(); } catch { }
        _yoloPanel = null;
        _yoloSession = null;
        _isDisposed = true;
    }

    /// Yolo-mode terminal panel for this conversation. Lazily built the first
    /// time the conversation enters yolo mode (Task Manager unchecked) and
    /// disposed in <see cref="Shutdown"/>. The panel hosts exactly one session
    /// running the selected tool's interactive yolo command.
    public TerminalPanelViewModel YoloPanel
    {
        get
        {
            if (_yoloPanel == null)
            {
                _yoloPanel = new TerminalPanelViewModel(
                    workingDirectoryProvider: () => _workingDirectory,
                    defaultShellIdProvider: () => null);
            }
            return _yoloPanel;
        }
    }

    /// Idempotently ensures a yolo CLI session is running for the current
    /// <see cref="Tool"/>. If a stale session for the wrong tool exists it's
    /// closed and replaced. Safe to call repeatedly (e.g. on tool change).
    public void EnsureYoloSession()
    {
        var panel = YoloPanel;
        if (_yoloSession != null && !_yoloSession.HasExited && _yoloSessionTool == Tool)
        {
            panel.ActiveSession = _yoloSession;
            return;
        }
        CloseYoloSession();
        var profile = YoloProfileFor(Tool, _currentSessionId);
        try
        {
            StartYoloSessionCapture(Tool);
            _yoloRecentOutput.Clear();
            _yoloResumeFailureSeen = false;
            _yoloLastStartUtc = DateTime.UtcNow;
            _yoloSession = panel.AddSession(profile);
            _yoloSessionTool = Tool;
            // Drive the sidebar busy indicator from output activity. A short
            // debounce keeps the dot lit during a streaming response and lets
            // it go dark once the agent stops printing.
            _yoloSession.Output += OnYoloOutput;
            _yoloSession.SessionExited += OnYoloSessionExited;
        }
        catch
        {
            // Spawn failures (CLI not installed) surface as a stderr banner
            // in the WebView2 via TerminalHost — nothing to do here.
        }
    }

    /// Closes the current yolo session if any. Called when the user re-enables
    /// Task Manager mode or switches tools while in yolo mode.
    public void CloseYoloSession()
    {
        var panel = _yoloPanel;
        var session = _yoloSession;
        _yoloSession = null;
        _yoloSessionWatcher?.Dispose();
        _yoloSessionWatcher = null;
        SetYoloBusy(false);
        if (session != null)
        {
            session.Output -= OnYoloOutput;
            session.SessionExited -= OnYoloSessionExited;
            try { panel?.CloseSession(session); } catch { }
        }
    }

    private void OnYoloOutput(object? sender, ReadOnlyMemory<byte> bytes)
    {
        // Sniff for "couldn't resume" errors so we can drop the dead session
        // id when the agent exits. We only need a small rolling window — these
        // errors print once near the top of the session before the agent
        // bails. Decoding inline is fine since this runs at PTY-output rate.
        if (!_yoloResumeFailureSeen && bytes.Length > 0)
        {
            try
            {
                var text = Encoding.UTF8.GetString(bytes.Span);
                _yoloRecentOutput.Append(text);
                if (_yoloRecentOutput.Length > 4096)
                    _yoloRecentOutput.Remove(0, _yoloRecentOutput.Length - 4096);
                if (LooksLikeResumeFailure(_yoloRecentOutput))
                    _yoloResumeFailureSeen = true;
            }
            catch { }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            SetYoloBusy(true);
            if (_yoloBusyTimer == null)
            {
                _yoloBusyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _yoloBusyTimer.Tick += (_, _) =>
                {
                    _yoloBusyTimer!.Stop();
                    SetYoloBusy(false);
                };
            }
            _yoloBusyTimer.Stop();
            _yoloBusyTimer.Start();
        });
    }

    private void OnYoloSessionExited(object? sender, EventArgs _)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            SetYoloBusy(false);
            var ranFor = DateTime.UtcNow - _yoloLastStartUtc;
            var resumeFailure = _yoloResumeFailureSeen;
            if (ReferenceEquals(sender, _yoloSession)) _yoloSession = null;

            // Only auto-restart if we're still in yolo mode for this conversation.
            if (!IsYoloModeEnabled) return;

            // Resume failure → drop the dead id and try fresh exactly once.
            if (resumeFailure)
            {
                CurrentSessionId = null;
                _yoloResumeFailureSeen = false;
                _yoloConsecutiveQuickExits = 0;
                EnsureYoloSession();
                return;
            }

            // Crash-loop guard: if the agent keeps dying within ~2s of spawn,
            // stop respawning so the user sees the failing terminal instead of
            // pegging the CPU on a tight restart loop (e.g. CLI not installed).
            if (ranFor < YoloQuickExitWindow)
            {
                _yoloConsecutiveQuickExits++;
                if (_yoloConsecutiveQuickExits >= YoloMaxConsecutiveQuickExits)
                    return;
            }
            else
            {
                _yoloConsecutiveQuickExits = 0;
            }

            EnsureYoloSession();
        });
    }

    private static bool LooksLikeResumeFailure(StringBuilder buf)
    {
        // Compare lower-case to be tolerant of casing variation across CLIs.
        // Patterns observed: claude prints "No conversation found with session
        // ID:"; codex/pi print similar "session not found" / "could not
        // resume" messages. We match any of them.
        var s = buf.ToString().ToLowerInvariant();
        return s.Contains("no conversation found with session")
            || s.Contains("session not found")
            || s.Contains("could not resume")
            || s.Contains("cannot resume")
            || s.Contains("unable to resume");
    }

    private void StartYoloSessionCapture(CliTool tool)
    {
        _yoloSessionWatcher?.Dispose();
        _yoloSessionWatcher = CreateYoloSessionWatcher(tool);
        if (_yoloSessionWatcher == null) return;

        _yoloSessionWatcher.SessionIdCaptured += (_, sid) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(sid))
                    CurrentSessionId = sid;
            });
        };
        _yoloSessionWatcher.Start();
    }

    private SessionFileWatcher? CreateYoloSessionWatcher(CliTool tool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwdKey = EncodeSessionPathKey(_workingDirectory);

        return tool switch
        {
            CliTool.ClaudeCode => new SessionFileWatcher(
                Path.Combine(home, ".claude", "projects"),
                path => path.IndexOf(cwdKey, StringComparison.OrdinalIgnoreCase) >= 0),
            CliTool.Codex => new SessionFileWatcher(
                Path.Combine(home, ".codex", "sessions")),
            CliTool.Pi => new SessionFileWatcher(
                Path.Combine(home, ".pi", "agent", "sessions"),
                path => path.IndexOf($"--{cwdKey}--", StringComparison.OrdinalIgnoreCase) >= 0),
            _ => null,
        };
    }

    private static string EncodeSessionPathKey(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var chars = normalized.Select(ch =>
            ch == ':' || ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar
                ? '-'
                : ch).ToArray();
        return new string(chars);
    }

    private void SetYoloBusy(bool value)
    {
        if (_yoloBusy == value) return;
        _yoloBusy = value;
        OnChanged(nameof(IsRunning));
    }

    /// Builds the CLI invocation for yolo mode. We launch through `cmd.exe /C`
    /// so Windows resolves npm `.cmd` shims (claude/codex/pi are installed as
    /// .cmd wrappers) without us having to walk PATH ourselves. /C exits cmd
    /// when the agent exits, which gives the conversation a clean exit signal
    /// to drive auto-restart in <see cref="OnYoloSessionExited"/>.
    private static ShellProfile YoloProfileFor(CliTool tool, string? sessionId)
    {
        var cmdExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var resumeId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var (id, label, args) = tool switch
        {
            CliTool.ClaudeCode => ("yolo-claude", "Claude Yolo", BuildClaudeYoloArgs(resumeId)),
            CliTool.Codex      => ("yolo-codex",  "Codex Yolo",  BuildCodexYoloArgs(resumeId)),
            CliTool.Pi         => ("yolo-pi",     "Pi Yolo",     BuildPiYoloArgs(resumeId)),
            _                  => ("yolo-claude", "Claude Yolo", BuildClaudeYoloArgs(resumeId)),
        };
        var inner = string.Join(" ", args.Select(QuoteCmdArg));
        return new ShellProfile(id, label, cmdExe, $"/C {inner}");
    }

    private static string[] BuildClaudeYoloArgs(string? sessionId)
    {
        var args = new List<string> { "claude", "--dangerously-skip-permissions" };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            args.Add("--resume");
            args.Add(sessionId);
        }
        return args.ToArray();
    }

    private static string[] BuildCodexYoloArgs(string? sessionId)
    {
        var args = new List<string> { "codex" };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            args.Add("resume");
            args.Add("--dangerously-bypass-approvals-and-sandbox");
            args.Add(sessionId);
        }
        else
        {
            args.Add("--dangerously-bypass-approvals-and-sandbox");
        }
        return args.ToArray();
    }

    private static string[] BuildPiYoloArgs(string? sessionId)
    {
        var args = new List<string> { "pi" };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            args.Add("--session");
            args.Add(sessionId);
        }
        return args.ToArray();
    }

    private static string QuoteCmdArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (!arg.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '&' or '|' or '<' or '>' or '^'))
            return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    public void Dispose() => Shutdown();

    // Debounce settings persistence. A lot of UI interactions (picking a
    // model, toggling a skill, typing in a field that binds on PropertyChanged)
    // cascade through settings setters. Writing JSON to disk on every pulse
    // was a real bottleneck — the debouncer collapses a flurry into one
    // write ~300 ms after the last change. A flush on Shutdown catches
    // anything still pending.
    private DispatcherTimer? _persistDebounce;
    private void PersistSettings()
    {
        if (_isDisposed) return;
        if (_persistDebounce == null)
        {
            _persistDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _persistDebounce.Tick += (_, _) => { _persistDebounce!.Stop(); _persistSettings(); };
        }
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    private void FlushPersistSettings()
    {
        if (_persistDebounce?.IsEnabled == true)
        {
            _persistDebounce.Stop();
            _persistSettings();
        }
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Batch-fire PropertyChanged for several property names in one call.
    /// Shaves off the repeated `OnChanged(nameof(...))` boilerplate where a
    /// setter or event handler touches multiple derived properties at once.
    private void OnChangedMany(params string[] names)
    {
        if (PropertyChanged is null) return;
        for (int i = 0; i < names.Length; i++)
            PropertyChanged.Invoke(this, new PropertyChangedEventArgs(names[i]));
    }
}
