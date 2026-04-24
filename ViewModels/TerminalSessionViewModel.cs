using System.ComponentModel;
using System.Runtime.CompilerServices;
using JustCode.Services;

namespace JustCode.ViewModels;

/// <summary>
/// One embedded terminal session — shell process, pseudo-console, and the
/// metadata that backs a tab in the Terminal panel's tab strip.
/// </summary>
public sealed class TerminalSessionViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SessionExited;

    private readonly ConPtyTerminal _pty = new();
    private string _title;
    private bool _isActive;
    private bool _isRenaming;
    private bool _hasExited;

    /// <summary>Stable id, used as the session id in bridge messages.</summary>
    public string Id { get; }

    public ShellProfile Shell { get; }

    public string WorkingDirectory { get; }

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value ?? ""; OnChanged(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnChanged(); }
    }

    /// Toggled by the UI when the user double-clicks a tab to rename it.
    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming == value) return; _isRenaming = value; OnChanged(); }
    }

    public bool HasExited => _hasExited;

    public event EventHandler<ReadOnlyMemory<byte>>? Output
    {
        add => _pty.Output += value;
        remove => _pty.Output -= value;
    }

    public TerminalSessionViewModel(string id, ShellProfile shell, string workingDirectory, string title)
    {
        Id = id;
        Shell = shell;
        WorkingDirectory = workingDirectory;
        _title = title;
        _pty.Exited += (_, _) =>
        {
            _hasExited = true;
            SessionExited?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Start(int cols, int rows)
    {
        _pty.Start(WorkingDirectory, Shell.Exe, Shell.Args, cols, rows);
    }

    public void Write(ReadOnlySpan<byte> bytes) => _pty.Write(bytes);
    public void Resize(int cols, int rows) => _pty.Resize(cols, rows);

    public void Dispose() => _pty.Dispose();

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
