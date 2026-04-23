using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    private string _text;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
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
    private string? _pendingConsoleHistory;
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
            OnChanged(nameof(IsPiTool));
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

    /// Sessions older than this are treated as too stale to resume cleanly.
    private static readonly TimeSpan SessionTTL = TimeSpan.FromHours(24);

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
    public long InputTokens { get => _inputTokens; private set { if (_inputTokens != value) { _inputTokens = value; OnChanged(); OnChanged(nameof(TokenSummary)); } } }
    public long OutputTokens { get => _outputTokens; private set { if (_outputTokens != value) { _outputTokens = value; OnChanged(); OnChanged(nameof(TokenSummary)); } } }
    public long CachedTokens { get => _cachedTokens; private set { if (_cachedTokens != value) { _cachedTokens = value; OnChanged(); OnChanged(nameof(TokenSummary)); } } }

    private int _toolCallCount;
    public int ToolCallCount
    {
        get => _toolCallCount;
        private set { if (_toolCallCount != value) { _toolCallCount = value; OnChanged(); } }
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

    /// -1 when the chat box holds a new draft; 0..Count-1 when navigating
    /// previously-queued items via Up/Down for editing. Edits to `ChatInput`
    /// while in recall mode are written through to the focused queue entry.
    private int _chatRecallIndex = -1;
    public int ChatRecallIndex
    {
        get => _chatRecallIndex;
        private set { if (_chatRecallIndex != value) { _chatRecallIndex = value; OnChanged(); OnChanged(nameof(IsRecallingQueued)); } }
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
        }
    }

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
        OnChanged(nameof(HasQueuedMessages));
        if (!IsRunning) _ = StartRunAsync(chatOnly: true);
    }

    public void RemoveQueued(QueuedChatMessage msg)
    {
        if (msg == null) return;
        int idx = QueuedMessages.IndexOf(msg);
        if (idx < 0) return;
        QueuedMessages.RemoveAt(idx);
        if (_chatRecallIndex == idx) ExitRecallMode();
        else if (_chatRecallIndex > idx) ChatRecallIndex = _chatRecallIndex - 1;
        OnChanged(nameof(HasQueuedMessages));
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
            OnChanged(nameof(HasQueuedMessages));
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
            OnChanged(nameof(HasQueuedMessages));
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

    // ≈ 3.7 chars per token is a reasonable English approximation.
    private static long CharsToTokens(long chars) =>
        chars <= 0 ? 0 : Math.Max(1L, (long)(chars / 3.7));

    public string TokenSummary
    {
        get
        {
            long inDisplay = _inputTokens + CharsToTokens(_estInputChars);
            long outDisplay = _outputTokens + CharsToTokens(_estOutputChars);
            if (inDisplay == 0 && outDisplay == 0) return "";
            var s = $"{inDisplay:N0} in · {outDisplay:N0} out";
            if (_cachedTokens > 0) s += $" · {_cachedTokens:N0} cached";
            if (_estInputChars > 0 || _estOutputChars > 0) s += " · est";
            return s;
        }
    }

    public ConversationViewModel(string workingDirectory, ConversationSettings settings, Action persistSettings)
    {
        _workingDirectory = workingDirectory;
        _settings = settings;
        _persistSettings = persistSettings;
        // Expire stale session ids on load so we never try to resume into a
        // context the model has likely forgotten.
        if (settings.LastSessionTimestamp is { } ts &&
            DateTime.UtcNow - ts > SessionTTL)
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
            OpenTasks = t.open;
            ClosedTasks = t.closed;
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
            OnChanged(nameof(TokenSummary));
        });
        _loopRunner.EstimatedInputCharsSet += (_, n) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // New iteration started. Reset the in-progress estimates to just
            // this iteration's input payload.
            _estInputChars = n;
            _estOutputChars = 0;
            OnChanged(nameof(TokenSummary));
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
                    OnChanged(nameof(TokenSummary));
                }
            });
        };

        _prompt = _promptStore.LoadPrompt();
        _promptStore.ExternalChange += (_, text) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyExternalPrompt(text));
        _promptStore.Watch();
        _tasksFile.Watch(TasksFile);
        _tasksText = _tasksFile.Load();
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
        OnChanged(nameof(TokenSummary));
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
            OnChanged(nameof(TotalElapsedText));
            OnChanged(nameof(IterationElapsedText));
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
        PinnedText = "";
        PinnedIsThinking = false;
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
        _loopRunner.Stop();
        _promptStore.FlushPrompt();
        FlushPersistSettings();
        if (_tasksSaveDebounce.IsEnabled)
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        }
        _tasksFile.Dispose();
        _promptStore.Dispose();
        _consoleLog.Dispose();
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
