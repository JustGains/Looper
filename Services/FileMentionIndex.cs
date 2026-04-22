using System.IO;

namespace JustCode.Services;

/// Enumerates files under a project's working directory for @-mention
/// autocomplete. Builds the index on a background thread on first
/// construction; Search returns whatever has been walked so far, with a
/// short grace wait for the build to finish.
public sealed class FileMentionIndex
{
    public string WorkingDirectory { get; }

    private readonly List<string> _list = new();
    private readonly object _lock = new();
    private readonly Task _buildTask;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".idea", ".vs", ".vscode",
        "node_modules", "bower_components", "vendor",
        "bin", "obj", "target", "dist", "build", "out",
        "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache",
        ".next", ".nuxt", ".cache", ".parcel-cache",
        ".looper", ".gradle", "DerivedData",
    };

    public FileMentionIndex(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
        _buildTask = Task.Run(Build);
    }

    public bool IsReady => _buildTask.IsCompleted;
    public int Count { get { lock (_lock) return _list.Count; } }

    public void Invalidate()
    {
        // Reset and kick off another build on a background task.
        // (Simple enough for explicit refresh calls; not called automatically.)
        lock (_lock) _list.Clear();
        Task.Run(Build);
    }

    public IReadOnlyList<string> Search(string query, int limit = 8)
    {
        // Give the initial build up to ~400ms to finish before we snapshot.
        // If it's still going, we'll search what's been walked so far.
        try { _buildTask.Wait(400); } catch { }

        List<string> snapshot;
        lock (_lock) snapshot = new List<string>(_list);

        if (string.IsNullOrEmpty(query))
            return snapshot.Take(limit).ToList();

        var q = query.Replace('\\', '/').ToLowerInvariant();
        var results = new List<(int rank, int len, string path)>();
        foreach (var p in snapshot)
        {
            var lower = p.ToLowerInvariant();
            int rank;
            if (lower == q) rank = 0;
            else if (Path.GetFileName(lower).StartsWith(q)) rank = 1;
            else if (lower.StartsWith(q)) rank = 2;
            else if (Path.GetFileName(lower).Contains(q)) rank = 3;
            else if (lower.Contains(q)) rank = 4;
            else continue;
            results.Add((rank, p.Length, p));
        }
        return results
            .OrderBy(r => r.rank)
            .ThenBy(r => r.len)
            .ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.path)
            .Take(limit)
            .ToList();
    }

    public static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Any(c => c is ' ' or '\t') ? $"\"{path}\"" : path;
    }

    private void Build()
    {
        try { Walk(WorkingDirectory, WorkingDirectory, depth: 0); }
        catch { }
    }

    private void Walk(string root, string dir, int depth)
    {
        if (depth > 32) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                lock (_lock) _list.Add(rel);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name)) continue;
                if (name.StartsWith('.') && !string.Equals(name, ".github", StringComparison.OrdinalIgnoreCase))
                    continue;
                Walk(root, sub, depth + 1);
            }
        }
        catch { }
    }
}
