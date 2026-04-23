using System.IO;
using System.Windows.Threading;

namespace JustCode.Services;

/// <summary>
/// Rolling per-conversation console history. Appends raw chunks, keeps the
/// last N complete lines on disk, and restores them on load so each
/// conversation's console survives restarts.
/// </summary>
public sealed class ConsoleLogStore : IDisposable
{
    private const int MaxLines = 500;

    private readonly string _path;
    private readonly DispatcherTimer _debounce;
    private readonly List<string> _lines = new();
    private string _partial = "";
    private readonly object _lock = new();

    public ConsoleLogStore(string path)
    {
        _path = path;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Flush(); };
    }

    /// Read the stored log. Populates the in-memory ring so subsequent
    /// `Append` calls can extend rather than replace.
    public string Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return "";
                var content = File.ReadAllText(_path);
                _lines.Clear();
                _partial = "";
                if (content.Length == 0) return "";
                var parts = content.Split('\n');
                // A trailing '\n' produces an empty final part; keep as completed line.
                for (int i = 0; i < parts.Length - 1; i++) _lines.Add(parts[i]);
                _partial = parts[^1];
                TrimToMax();
                return Render();
            }
            catch { return ""; }
        }
    }

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        lock (_lock)
        {
            var combined = _partial + chunk;
            var parts = combined.Split('\n');
            for (int i = 0; i < parts.Length - 1; i++) _lines.Add(parts[i]);
            _partial = parts[^1];
            TrimToMax();
        }
        _debounce.Stop();
        _debounce.Start();
    }

    public void Flush()
    {
        string content;
        lock (_lock) { content = Render(); }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, content);
        }
        catch { }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
            _partial = "";
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private string Render()
    {
        var body = string.Join('\n', _lines);
        if (_lines.Count == 0) return _partial;
        return body + (string.IsNullOrEmpty(_partial) ? "" : "\n" + _partial);
    }

    private void TrimToMax()
    {
        if (_lines.Count > MaxLines)
            _lines.RemoveRange(0, _lines.Count - MaxLines);
    }

    public void Dispose()
    {
        _debounce.Stop();
        Flush();
    }
}
