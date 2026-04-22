using System.IO;
using System.Windows.Threading;

namespace JustCode.Services;

public sealed class PromptStore : IDisposable
{
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _fsDebounce;
    private readonly DispatcherTimer _poller;
    private string _pendingPrompt = "";
    private readonly string _looperDir;
    private readonly string _promptFile;
    private FileSystemWatcher? _watcher;
    private DateTime _suppressUntil = DateTime.MinValue;
    private string _lastEmitted = "";

    /// Fires when the plan file changes on disk due to something outside of
    /// `SavePromptDebounced` (e.g. the model's Edit tool updating it in PLAN
    /// mode). Consumers should marshal to the UI thread and reload the view.
    public event EventHandler<string>? ExternalChange;

    public PromptStore(string looperDir, string promptFile)
    {
        _looperDir = looperDir;
        _promptFile = promptFile;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            WritePromptNow(_pendingPrompt);
        };

        _fsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _fsDebounce.Tick += (_, _) => { _fsDebounce.Stop(); RaiseIfChanged(); };

        // Polling backstop: FileSystemWatcher misses temp-file→rename patterns
        // on some filesystems. A 1.25 s tick catches those reliably.
        _poller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1250) };
        _poller.Tick += (_, _) => RaiseIfChanged();
    }

    public string LoadPrompt()
    {
        var s = SafeRead();
        _lastEmitted = s;
        return s;
    }

    public void SavePromptDebounced(string text)
    {
        _pendingPrompt = text;
        _debounce.Stop();
        _debounce.Start();
    }

    public void FlushPrompt()
    {
        if (_debounce.IsEnabled)
        {
            _debounce.Stop();
            WritePromptNow(_pendingPrompt);
        }
    }

    /// Start watching the plan file on disk. Safe to call multiple times;
    /// the prior watcher is torn down first.
    public void Watch()
    {
        Unwatch();
        var dir = Path.GetDirectoryName(_promptFile);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        if (!File.Exists(_promptFile))
            File.WriteAllText(_promptFile, "");

        _lastEmitted = SafeRead();

        _watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime
                         | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsRenamed;

        _poller.Start();
    }

    public void Unwatch()
    {
        _poller.Stop();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void WritePromptNow(string text)
    {
        try
        {
            Directory.CreateDirectory(_looperDir);
            // Suppress self-triggered FS events so our own debounced saves
            // don't round-trip back as "external" changes.
            _suppressUntil = DateTime.UtcNow.AddMilliseconds(400);
            var tmp = _promptFile + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _promptFile, overwrite: true);
            _lastEmitted = text;
        }
        catch { }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, Path.GetFileName(_promptFile), StringComparison.OrdinalIgnoreCase))
            return;
        ScheduleDebounced();
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        if (!string.Equals(e.Name, Path.GetFileName(_promptFile), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.OldName, Path.GetFileName(_promptFile), StringComparison.OrdinalIgnoreCase))
            return;
        ScheduleDebounced();
    }

    private void ScheduleDebounced()
    {
        if (DateTime.UtcNow < _suppressUntil) return;
        _fsDebounce.Stop();
        _fsDebounce.Start();
    }

    private void RaiseIfChanged()
    {
        if (DateTime.UtcNow < _suppressUntil) return;
        var current = SafeRead();
        if (current == _lastEmitted) return;
        _lastEmitted = current;
        ExternalChange?.Invoke(this, current);
    }

    private string SafeRead()
    {
        try { return File.Exists(_promptFile) ? File.ReadAllText(_promptFile) : ""; }
        catch { return ""; }
    }

    public void Dispose() => Unwatch();
}
