using System.IO;
using System.Windows.Threading;

namespace Looper.Services;

public sealed class TasksFileService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poller;
    private string _path = "";
    private DateTime _suppressUntil = DateTime.MinValue;
    private string _lastEmitted = "";

    public event EventHandler<string>? ExternalChange;

    public TasksFileService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RaiseIfChanged();
        };

        // Polling backstop: FileSystemWatcher misses some writers (temp+rename,
        // certain filesystems, etc). A 1.25s poll compares mtime+size+content
        // and fires ExternalChange if anything differs from what we last saw.
        _poller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1250) };
        _poller.Tick += (_, _) => RaiseIfChanged();
    }

    public void Watch(string path)
    {
        Unwatch();
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return;

        Directory.CreateDirectory(dir);
        if (!File.Exists(path))
            File.WriteAllText(path, "");

        _lastEmitted = SafeRead();

        // Watch the *directory* without a filename filter so we catch
        // temp-file → rename patterns too; we filter in the handler.
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

    public string Load()
    {
        var s = SafeRead();
        _lastEmitted = s;
        return s;
    }

    public void Save(string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _suppressUntil = DateTime.UtcNow.AddMilliseconds(400);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, _path, overwrite: true);
            _lastEmitted = content;
        }
        catch { }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, Path.GetFileName(_path), StringComparison.OrdinalIgnoreCase))
            return;
        ScheduleDebounced();
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        if (!string.Equals(e.Name, Path.GetFileName(_path), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.OldName, Path.GetFileName(_path), StringComparison.OrdinalIgnoreCase))
            return;
        ScheduleDebounced();
    }

    private void ScheduleDebounced()
    {
        if (DateTime.UtcNow < _suppressUntil) return;
        _debounce.Stop();
        _debounce.Start();
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
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path) : "";
        }
        catch { return ""; }
    }

    public void Dispose() => Unwatch();
}
