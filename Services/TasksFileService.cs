using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace JustCode.Services;

public enum TaskViewFilter
{
    All,
    Open,
    Done,
    Summaries,
    LatestSummary,
}

/// Per-tasks.md statistics used to build loop context prompts and task-pane UI.
public readonly record struct TaskStats(int Open, int Closed, string? LastSummary, int SummaryCount = 0)
{
    public int Total => Open + Closed;
}

public readonly record struct TaskProjection(TaskStats Stats, string Content);
public readonly record struct TaskDocumentAnalysis(TaskStats Stats, IReadOnlyList<string> SummarySections);

public static class TasksMarkdownAnalyzer
{
    // Captures both flush and indented checkbox lines, but excludes date
    // stamps like `[2026-01-29]` by requiring the brackets to contain only a
    // single space or 'x'/'X'.
    private static readonly Regex OpenBox = new(@"^[ \t]*- \[[ ]\] ", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ClosedBox = new(@"^[ \t]*- \[[xX]\] ", RegexOptions.Multiline | RegexOptions.Compiled);

    /// Finds the most recent `### <anything> — session summary` heading and
    /// returns everything up to the next blank line or next heading.
    private static readonly Regex SummaryBlock = new(
        @"^###\s+.*?session summary\s*$(?<body>(?:\r?\n(?!#|$).*)*)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TaskStats Analyze(string? content)
        => AnalyzeDocument(content).Stats;

    public static TaskDocumentAnalysis AnalyzeDocument(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new TaskDocumentAnalysis(new TaskStats(0, 0, null, 0), Array.Empty<string>());

        int open = OpenBox.Matches(content).Count;
        int closed = ClosedBox.Matches(content).Count;

        var summarySections = ExtractSummarySections(content);
        string? summary = null;
        int summaryCount = summarySections.Count;
        if (summaryCount > 0)
        {
            var body = summarySections[summaryCount - 1];
            if (!string.IsNullOrWhiteSpace(body))
            {
                // Keep it compact — 240 char cap so loop-context prompts don't
                // balloon after dozens of iterations.
                summary = body.Length > 240 ? body.Substring(0, 240) + "…" : body;
            }
        }

        return new TaskDocumentAnalysis(new TaskStats(open, closed, summary, summaryCount), summarySections);
    }

    public static TaskProjection CreateProjection(string? content, TaskViewFilter filter)
    {
        var source = content ?? "";
        var analysis = AnalyzeDocument(source);
        var stats = analysis.Stats;
        var projected = filter switch
        {
            TaskViewFilter.Open => BuildCheckboxProjection(source, OpenBox, "Open tasks", stats.Open),
            TaskViewFilter.Done => BuildCheckboxProjection(source, ClosedBox, "Completed tasks", stats.Closed),
            TaskViewFilter.Summaries => BuildSummaryProjection(analysis.SummarySections, stats.SummaryCount),
            TaskViewFilter.LatestSummary => BuildLatestSummaryProjection(analysis.SummarySections),
            _ => string.IsNullOrWhiteSpace(source) ? "_No tasks yet._" : source,
        };
        return new TaskProjection(stats, projected);
    }

    private static string BuildCheckboxProjection(string content, Regex matcher, string heading, int count)
    {
        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (matcher.IsMatch(line))
                lines.Add(line);
        }

        if (lines.Count == 0)
            return $"### {heading} ({count})\n\n_None._";

        return $"### {heading} ({count})\n\n" + string.Join(Environment.NewLine, lines);
    }

    private static List<string> ExtractSummarySections(string content)
    {
        var matches = SummaryBlock.Matches(content);
        var sections = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var block = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(block))
                sections.Add(block);
        }

        return sections;
    }

    private static string BuildSummaryProjection(IReadOnlyList<string> sections, int summaryCount)
    {
        if (sections.Count == 0)
            return "### Session summaries (0)\n\n_None yet._";

        if (sections.Count == 0)
            return $"### Session summaries ({summaryCount})\n\n_None yet._";

        return $"### Session summaries ({summaryCount})\n\n" + string.Join(
            Environment.NewLine + Environment.NewLine,
            sections);
    }

    private static string BuildLatestSummaryProjection(IReadOnlyList<string> sections)
    {
        if (sections.Count == 0)
            return "### Latest session summary\n\n_None yet._";

        return "### Latest session summary\n\n" + sections[^1];
    }
}

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
