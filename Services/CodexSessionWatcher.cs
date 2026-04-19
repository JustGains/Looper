using System.IO;

namespace Looper.Services;

/// Watches ~/.codex/sessions/ for newly-created .jsonl session files.
/// The filename (without extension) IS the session id that
/// `codex exec resume <id>` expects.
public sealed class CodexSessionWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private string? _captured;

    public event EventHandler<string>? SessionIdCaptured;
    public string? CapturedSessionId => _captured;

    public string SessionsRoot { get; }

    public CodexSessionWatcher()
    {
        SessionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "sessions");
    }

    public void Start()
    {
        Stop();
        _captured = null;
        try { Directory.CreateDirectory(SessionsRoot); } catch { return; }

        _watcher = new FileSystemWatcher(SessionsRoot, "*.jsonl")
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
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Capture(e.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs e) => Capture(e.FullPath);

    private void Capture(string fullPath)
    {
        if (_captured != null) return; // only latch the first one per run
        try
        {
            var name = Path.GetFileNameWithoutExtension(fullPath);
            // Codex filenames often prefix with "rollout-" and a timestamp;
            // the tail portion is a UUID. Extract the last UUID-shaped token.
            var id = ExtractUuid(name) ?? name;
            if (!string.IsNullOrWhiteSpace(id))
            {
                _captured = id;
                SessionIdCaptured?.Invoke(this, id);
            }
        }
        catch { }
    }

    private static string? ExtractUuid(string s)
    {
        // Pull the right-most 36-char uuid pattern (8-4-4-4-12 hex).
        var m = System.Text.RegularExpressions.Regex.Match(s,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    public void Dispose() => Stop();
}
