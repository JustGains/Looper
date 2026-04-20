using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Looper.Models;
using Looper.Services;

namespace Looper.ViewModels;

public sealed class ProjectViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ConsoleAppend;

    private readonly LoopSettings _shellSettings;
    private readonly PromptStore _promptStore;
    private readonly TasksFileService _tasksFile;
    private readonly LoopRunner _loopRunner;
    private readonly DispatcherTimer _tasksSaveDebounce;
    private readonly DispatcherTimer _uiTick;
    private DateTime? _iterStartUtc;
    private DateTime? _totalStartUtc;
    private bool _suppressTasksSave;
    private string? _promptAtLastInjection;

    public string WorkingDirectory { get; }
    public string LooperDir => Path.Combine(WorkingDirectory, ".looper");
    public string PromptFile => Path.Combine(LooperDir, "prompt.txt");
    public string TasksFile => Path.Combine(LooperDir, "tasks.md");

    public string HeaderLabel
    {
        get
        {
            var trimmed = WorkingDirectory.TrimEnd('\\', '/');
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
    }
    public string HeaderTooltip => WorkingDirectory;

    public FlowDocument ConsoleDocument { get; }
    public Paragraph ConsoleParagraph { get; }

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
        $"Iteration: {CurrentIteration} / {(_shellSettings.RalphEnabled ? _shellSettings.MaxIterations : 1)}";
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

    public ProjectViewModel(string workingDirectory, LoopSettings shellSettings)
    {
        WorkingDirectory = ConfigStore.Normalize(workingDirectory);
        _shellSettings = shellSettings;

        ConsoleParagraph = new Paragraph { Margin = new Thickness(0), TextIndent = 0 };
        ConsoleDocument = new FlowDocument(ConsoleParagraph)
        {
            PageWidth = 6000,
            Background = System.Windows.Media.Brushes.Transparent,
        };

        _promptStore = new PromptStore(LooperDir, PromptFile);
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
            await _loopRunner.RunAsync(() => _prompt, _shellSettings, WorkingDirectory);
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

    public void NotifyMaxIterChanged() => OnChanged(nameof(IterationLabel));

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
