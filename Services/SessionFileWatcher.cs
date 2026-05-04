using System.IO;
using System.Text.RegularExpressions;

namespace JustCode.Services;

/// Watches a CLI's session storage for newly-created jsonl files and extracts
/// the session UUID from the filename.
public sealed class SessionFileWatcher : IDisposable
{
    private static readonly Regex UuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private readonly string _root;
    private readonly Func<string, bool>? _pathFilter;
    private FileSystemWatcher? _watcher;
    private string? _captured;

    public event EventHandler<string>? SessionIdCaptured;

    public SessionFileWatcher(string root, Func<string, bool>? pathFilter = null)
    {
        _root = root;
        _pathFilter = pathFilter;
    }

    public void Start()
    {
        Stop();
        _captured = null;
        try { Directory.CreateDirectory(_root); } catch { return; }

        _watcher = new FileSystemWatcher(_root, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
    }

    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Capture(e.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs e) => Capture(e.FullPath);

    private void Capture(string fullPath)
    {
        if (_captured != null) return;
        try
        {
            if (_pathFilter != null && !_pathFilter(fullPath)) return;
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var id = ExtractLastUuid(name) ?? name;
            if (string.IsNullOrWhiteSpace(id)) return;
            _captured = id;
            SessionIdCaptured?.Invoke(this, id);
        }
        catch { }
    }

    public static string? ExtractLastUuid(string s)
    {
        var matches = UuidRegex.Matches(s);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    public void Dispose() => Stop();
}
