using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using JustCode.Services;

namespace JustCode.ViewModels;

/// <summary>
/// Holds a project's embedded terminal sessions and the currently-active one.
/// Each ProjectViewModel owns one TerminalPanelViewModel; switching projects
/// swaps the entire tab strip, mirroring VS Code's per-workspace terminals.
/// </summary>
public sealed class TerminalPanelViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<TerminalSessionViewModel>? SessionAdded;
    public event EventHandler<TerminalSessionViewModel>? SessionRemoved;
    public event EventHandler<TerminalSessionViewModel>? ActiveSessionChanged;

    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = new();

    private readonly Func<string> _workingDirectoryProvider;
    private readonly Func<string?> _defaultShellIdProvider;
    private TerminalSessionViewModel? _active;
    private int _nextNumber = 1;

    public TerminalPanelViewModel(
        Func<string> workingDirectoryProvider,
        Func<string?> defaultShellIdProvider)
    {
        _workingDirectoryProvider = workingDirectoryProvider;
        _defaultShellIdProvider = defaultShellIdProvider;

        AddSessionCommand = new RelayCommand(_ => AddSession());
        CloseActiveSessionCommand = new RelayCommand(_ =>
        {
            if (_active != null) CloseSession(_active);
        });
    }

    public TerminalSessionViewModel? ActiveSession
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            if (_active != null) _active.IsActive = false;
            _active = value;
            if (_active != null) _active.IsActive = true;
            OnChanged();
            OnChanged(nameof(HasActiveSession));
            if (_active != null) ActiveSessionChanged?.Invoke(this, _active);
        }
    }

    public bool HasActiveSession => _active != null;
    public bool HasAnySessions => Sessions.Count > 0;

    public ICommand AddSessionCommand { get; }
    public ICommand CloseActiveSessionCommand { get; }

    /// Opens a new session using the default shell. Returns it.
    public TerminalSessionViewModel AddSession() => AddSession((string?)null);

    /// Opens a new session using a specific shell id (or default if null).
    public TerminalSessionViewModel AddSession(string? shellId)
    {
        var shell = ShellDetector.Resolve(shellId ?? _defaultShellIdProvider());
        return AddSession(shell);
    }

    /// Opens a new session using an explicit shell profile, bypassing
    /// <see cref="ShellDetector"/>. Used for spawning CLI-as-shell sessions
    /// (Claude/Codex/Pi yolo mode) where the "shell" is the agent CLI itself.
    public TerminalSessionViewModel AddSession(ShellProfile shell)
    {
        var cwd = _workingDirectoryProvider();
        if (string.IsNullOrWhiteSpace(cwd)) cwd = Environment.CurrentDirectory;
        var id = Guid.NewGuid().ToString("N");
        var title = $"{shell.Label} ({_nextNumber++})";
        var session = new TerminalSessionViewModel(id, shell, cwd, title);
        session.SessionExited += OnSessionExited;
        Sessions.Add(session);
        OnChanged(nameof(HasAnySessions));
        SessionAdded?.Invoke(this, session);
        ActiveSession = session;
        return session;
    }

    public void CloseSession(TerminalSessionViewModel session)
    {
        if (!Sessions.Contains(session)) return;
        session.SessionExited -= OnSessionExited;
        var idx = Sessions.IndexOf(session);
        Sessions.Remove(session);
        try { session.Dispose(); } catch { }
        SessionRemoved?.Invoke(this, session);
        OnChanged(nameof(HasAnySessions));

        if (ReferenceEquals(_active, session))
        {
            if (Sessions.Count == 0) ActiveSession = null;
            else ActiveSession = Sessions[Math.Min(idx, Sessions.Count - 1)];
        }
    }

    /// Fire-and-forget: write a command into the active session's stdin,
    /// creating a session first if the panel is empty. Used by "run script"
    /// menu items and the file-explorer "Open terminal here" action.
    public void RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        var session = _active ?? AddSession();
        var bytes = System.Text.Encoding.UTF8.GetBytes(command + "\r");
        session.Write(bytes);
    }

    public void Dispose()
    {
        foreach (var s in Sessions.ToArray())
        {
            try { s.Dispose(); } catch { }
        }
        Sessions.Clear();
    }

    private void OnSessionExited(object? sender, EventArgs e)
    {
        if (sender is TerminalSessionViewModel s)
        {
            // Auto-close on exit so the tab disappears once the shell exits.
            // Marshal to the UI thread so ObservableCollection mutation is safe.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => CloseSession(s));
            else
                CloseSession(s);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// Minimal ICommand. Only used inside this file for now.
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
