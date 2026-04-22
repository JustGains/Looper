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
        if (t.Length == 0) return;
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
    private string? TryDequeueQueued()
    {
        while (QueuedMessages.Count > 0)
        {
            var head = QueuedMessages[0];
            QueuedMessages.RemoveAt(0);
            if (_chatRecallIndex == 0) ExitRecallMode();
            else if (_chatRecallIndex > 0) ChatRecallIndex = _chatRecallIndex - 1;
            OnChanged(nameof(HasQueuedMessages));
            if (!string.IsNullOrWhiteSpace(head.Text))
                return ExpandPromptForSubmission(head.Text);
        }
        return null;
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
            CurrentSessionId = sid;
        });
        _loopRunner.ToolCallInvoked += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ToolCallCount++;
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

    public Task ToggleStartStopAsync()
    {
        if (IsRunning)
        {
            _loopRunner.Stop();
            return Task.CompletedTask;
        }
        return StartRunAsync(chatOnly: false);
    }

    private async Task StartRunAsync(bool chatOnly)
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
                chatOnly: chatOnly);
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
        if (_tasksSaveDebounce.IsEnabled)
        {
            _tasksSaveDebounce.Stop();
            _tasksFile.Save(_tasksText);
        }
        _tasksFile.Dispose();
        _consoleLog.Dispose();
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
