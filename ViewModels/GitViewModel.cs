using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using JustCode.Models;
using JustCode.Services;

namespace JustCode.ViewModels;

public enum GitSectionKind { Staged, Unstaged }

public sealed class GitSectionHeader : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label { get; init; } = "";
    public GitSectionKind Kind { get; init; }
    public bool IsStaged => Kind == GitSectionKind.Staged;
    public bool IsUnstaged => Kind == GitSectionKind.Unstaged;

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountLabel)));
        }
    }
    public string CountLabel => $"({_count})";
}

public sealed class GitFileRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public GitFileChange Change { get; }
    public bool IsStagedGroup { get; }
    public bool IsUnstagedGroup => !IsStagedGroup;
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    public string FullPath => Change.Path;

    /// Single-letter status indicator (M/A/D/U/R/?)
    public string StatusLetter => (IsStagedGroup ? Change.IndexKind : Change.WorkingKind) switch
    {
        GitChangeKind.Untracked => "U",
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Copied => "C",
        GitChangeKind.TypeChanged => "T",
        GitChangeKind.Conflicted => "!",
        _ => "?",
    };

    // Shared, frozen brushes per status kind. Previously each row had its
    // own `<SolidColorBrush Color="{Binding StatusColor}"/>` in XAML, which
    // allocated an unfrozen brush per row per refresh — not shareable across
    // threads, GC pressure, and WPF couldn't batch-render text runs because
    // every brush was a unique instance.
    private static readonly System.Windows.Media.Brush _brushEmerald = Freeze("#34d399");
    private static readonly System.Windows.Media.Brush _brushAmber   = Freeze("#fbbf24");
    private static readonly System.Windows.Media.Brush _brushRose    = Freeze("#f87171");
    private static readonly System.Windows.Media.Brush _brushBlue    = Freeze("#7dc4ff");
    private static readonly System.Windows.Media.Brush _brushDim     = Freeze("#76767c");

    public System.Windows.Media.Brush StatusBrush => (IsStagedGroup ? Change.IndexKind : Change.WorkingKind) switch
    {
        GitChangeKind.Untracked => _brushEmerald,
        GitChangeKind.Added => _brushEmerald,
        GitChangeKind.Modified => _brushAmber,
        GitChangeKind.Deleted => _brushRose,
        GitChangeKind.Renamed => _brushBlue,
        GitChangeKind.Copied => _brushBlue,
        GitChangeKind.Conflicted => _brushRose,
        _ => _brushDim,
    };

    private static System.Windows.Media.Brush Freeze(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // Material-theme file icon, async-loaded off the UI thread (same pattern
    // as FileNode). We return the generic file icon immediately so the Git
    // list never looks blank while the specific SVG parses.
    private static readonly System.Windows.Media.ImageSource? _fallbackIcon =
        FileIconService.GetFileIcon("file");
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconLoadStarted;
    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            if (!_iconLoadStarted)
            {
                _iconLoadStarted = true;
                LoadIconAsync();
            }
            return _icon ?? _fallbackIcon;
        }
    }

    private async void LoadIconAsync()
    {
        var name = FileName;
        var img = await Task.Run(() =>
            (System.Windows.Media.ImageSource?)FileIconService.GetFileIcon(name));
        if (img == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        await dispatcher.InvokeAsync(() =>
        {
            _icon = img;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        });
    }

    public GitFileRow(GitFileChange change, bool isStagedGroup)
    {
        Change = change;
        IsStagedGroup = isStagedGroup;
    }
}

public sealed class GitViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _workingDirectory;
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _gitWatcher;
    private readonly Func<ConversationSettings?> _activeConvSettings;

    public string WorkingDirectory => _workingDirectory;

    public ObservableCollection<GitFileRow> StagedChanges { get; } = new();
    public ObservableCollection<GitFileRow> UnstagedChanges { get; } = new();
    public ObservableCollection<GitCommit> RecentCommits { get; } = new();
    public ObservableCollection<string> Branches { get; } = new();

    // Flat, virtualization-friendly view of both change sections. The panel
    // binds a single ItemsControl to this list so the WPF VirtualizingStackPanel
    // can actually virtualize; with two separate ItemsControls stacked in a
    // StackPanel the inner panels got infinite measure height and materialized
    // every row, which froze the UI whenever a repo had hundreds of changes.
    private readonly GitSectionHeader _stagedHeader = new() { Label = "STAGED CHANGES", Kind = GitSectionKind.Staged };
    private readonly GitSectionHeader _unstagedHeader = new() { Label = "CHANGES", Kind = GitSectionKind.Unstaged };
    public ObservableCollection<object> AllRows { get; } = new();

    public bool IsRepo => GitService.IsRepo(_workingDirectory);

    private string? _currentBranch;
    public string? CurrentBranch
    {
        get => _currentBranch;
        private set { if (_currentBranch != value) { _currentBranch = value; OnChanged(); OnChanged(nameof(BranchLabel)); } }
    }

    private string? _upstream;
    public string? Upstream
    {
        get => _upstream;
        private set { if (_upstream != value) { _upstream = value; OnChanged(); OnChanged(nameof(HasUpstream)); OnChanged(nameof(SyncLabel)); } }
    }
    public bool HasUpstream => !string.IsNullOrEmpty(_upstream);

    private int _ahead, _behind;
    public int Ahead { get => _ahead; private set { if (_ahead != value) { _ahead = value; OnChanged(); OnChanged(nameof(SyncLabel)); } } }
    public int Behind { get => _behind; private set { if (_behind != value) { _behind = value; OnChanged(); OnChanged(nameof(SyncLabel)); } } }

    public string BranchLabel => string.IsNullOrEmpty(_currentBranch) ? "—" : _currentBranch!;

    public string SyncLabel
    {
        get
        {
            if (!HasUpstream) return "publish";
            if (_ahead == 0 && _behind == 0) return "in sync";
            var sb = new StringBuilder();
            if (_behind > 0) sb.Append('↓').Append(_behind);
            if (_ahead > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append('↑').Append(_ahead); }
            return sb.ToString();
        }
    }

    public int TotalChangeCount => StagedChanges.Count + UnstagedChanges.Count;
    public string ChangeCountLabel => TotalChangeCount == 0
        ? "No changes"
        : $"{TotalChangeCount} change{(TotalChangeCount == 1 ? "" : "s")}";

    public bool CanCommit => StagedChanges.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    private string _commitMessage = "";
    public string CommitMessage
    {
        get => _commitMessage;
        set { if (_commitMessage != value) { _commitMessage = value; OnChanged(); OnChanged(nameof(CanCommit)); } }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set { if (_lastError != value) { _lastError = value; OnChanged(); OnChanged(nameof(HasError)); } }
    }
    public bool HasError => !string.IsNullOrEmpty(_lastError);

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy != value) { _isBusy = value; OnChanged(); } }
    }

    private bool _isGeneratingMessage;
    public bool IsGeneratingMessage
    {
        get => _isGeneratingMessage;
        private set { if (_isGeneratingMessage != value) { _isGeneratingMessage = value; OnChanged(); } }
    }

    private bool _isActive;
    /// Gate refresh/watcher activity. When false the VM is a quiet shell —
    /// no timer, no FS watcher, no process spawning. Flip to true only when
    /// the Git tab is actually visible for the current project so we don't
    /// pay for a poll on every project in every tab.
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value) Start(); else Stop();
        }
    }

    public GitViewModel(string workingDirectory, Func<ConversationSettings?> activeConvSettings)
    {
        _workingDirectory = workingDirectory;
        _activeConvSettings = activeConvSettings;

        // Timer exists but is not started until IsActive flips on. Spawning
        // `git status` every 8s per inactive project added up fast.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    private void Start()
    {
        if (!IsRepo) return;
        _refreshTimer.Start();
        TryStartWatcher();
        _ = RefreshAsync();
    }

    private void Stop()
    {
        _refreshTimer.Stop();
        _gitWatcher?.Dispose();
        _gitWatcher = null;
    }

    private void TryStartWatcher()
    {
        try
        {
            var gitDir = System.IO.Path.Combine(_workingDirectory, ".git");
            if (!Directory.Exists(gitDir)) return; // may be a worktree; skip
            _gitWatcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _gitWatcher.Changed += (_, _) => DebouncedRefresh();
            _gitWatcher.Created += (_, _) => DebouncedRefresh();
            _gitWatcher.Deleted += (_, _) => DebouncedRefresh();
        }
        catch { }
    }

    // True debounce — a single DispatcherTimer is restarted on every FS
    // event. Previous implementation queued a UI task per event and relied
    // on a `DateTime` check to bail, which still flooded the dispatcher
    // with hundreds of short-lived tasks during a commit.
    private DispatcherTimer? _debounceTimer;
    private void DebouncedRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _debounceTimer.Tick += async (_, _) =>
                {
                    _debounceTimer!.Stop();
                    await RefreshAsync();
                };
            }
            // Restart → the timer only fires 250ms after the LAST event.
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }));
    }

    public async Task RefreshAsync()
    {
        if (!IsRepo || !_isActive) return;
        try
        {
            // Fire all three git queries concurrently. They don't depend on
            // each other, and on Windows each `git ...` spawn is ~30-60 ms
            // of Process.Start overhead — sequential was 2/3 wasted time.
            var statusTask = GitService.GetStatusAsync(_workingDirectory);
            var branchesTask = GitService.GetBranchesAsync(_workingDirectory);
            var commitsTask = GitService.GetRecentCommitsAsync(_workingDirectory, 30);
            await Task.WhenAll(statusTask, branchesTask, commitsTask);
            var status = statusTask.Result;
            var branches = branchesTask.Result;
            var commits = commitsTask.Result;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                CurrentBranch = status.CurrentBranch;
                Upstream = status.Upstream;
                Ahead = status.Ahead;
                Behind = status.Behind;

                // Smart diff: preserve existing rows (and their async-loaded
                // icons) when the file + status hasn't changed. Previously
                // every refresh Clear()+Add()'d the whole list, which tore
                // down and rebuilt every WPF row container + re-kicked every
                // icon load. This turns the 8s-tick into a no-op when the
                // working tree is quiet.
                var newStaged = status.Changes
                    .Where(c => c.HasStaged)
                    .Select(c => new GitFileRow(c, isStagedGroup: true));
                SyncRows(StagedChanges, newStaged);
                var newUnstaged = status.Changes
                    .Where(c => c.HasUnstaged)
                    .Select(c => new GitFileRow(c, isStagedGroup: false));
                SyncRows(UnstagedChanges, newUnstaged);

                OnChanged(nameof(TotalChangeCount));
                OnChanged(nameof(ChangeCountLabel));
                OnChanged(nameof(CanCommit));

                SyncStrings(Branches, branches);
                SyncCommits(RecentCommits, commits);
                RebuildAllRows();
            });
        }
        catch (Exception ex) { SetError(ex.Message); }
    }

    // Compose AllRows = [stagedHeader?, ...StagedChanges, unstagedHeader?, ...UnstagedChanges].
    // Reference-equality fast path so quiet refreshes are a no-op and virtualized
    // containers aren't torn down unnecessarily.
    private void RebuildAllRows()
    {
        _stagedHeader.Count = StagedChanges.Count;
        _unstagedHeader.Count = UnstagedChanges.Count;

        var next = new List<object>(2 + StagedChanges.Count + UnstagedChanges.Count);
        if (StagedChanges.Count > 0)
        {
            next.Add(_stagedHeader);
            foreach (var r in StagedChanges) next.Add(r);
        }
        if (UnstagedChanges.Count > 0)
        {
            next.Add(_unstagedHeader);
            foreach (var r in UnstagedChanges) next.Add(r);
        }

        if (AllRows.Count == next.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count; i++)
                if (!ReferenceEquals(AllRows[i], next[i])) { same = false; break; }
            if (same) return;
        }
        AllRows.Clear();
        foreach (var o in next) AllRows.Add(o);
    }

    /// In-place sync for the staged/unstaged file lists. Identity is
    /// (path, status letter) — if either changes, the row is replaced so
    /// WPF re-materializes its container; otherwise the row is kept and
    /// its icon cache survives, so rapid refreshes don't flicker or spam
    /// the thread pool with SVG re-parses.
    private static void SyncRows(ObservableCollection<GitFileRow> current, IEnumerable<GitFileRow> desired)
    {
        var desiredList = desired.ToList();
        // Fast path: same length + same keys in same order → nothing to do.
        if (current.Count == desiredList.Count)
        {
            bool allMatch = true;
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i].FullPath != desiredList[i].FullPath ||
                    current[i].StatusLetter != desiredList[i].StatusLetter)
                { allMatch = false; break; }
            }
            if (allMatch) return;
        }
        // General case: index existing rows by key, rebuild in the new order
        // but reuse instances wherever possible.
        var existing = new Dictionary<string, GitFileRow>(current.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var row in current) existing[row.FullPath + "|" + row.StatusLetter] = row;

        var next = new List<GitFileRow>(desiredList.Count);
        foreach (var d in desiredList)
        {
            var key = d.FullPath + "|" + d.StatusLetter;
            next.Add(existing.TryGetValue(key, out var reuse) ? reuse : d);
        }

        // Apply minimal edits: remove rows no longer present, then align.
        var nextKeys = new HashSet<string>(next.Select(r => r.FullPath + "|" + r.StatusLetter), StringComparer.OrdinalIgnoreCase);
        for (int i = current.Count - 1; i >= 0; i--)
        {
            var k = current[i].FullPath + "|" + current[i].StatusLetter;
            if (!nextKeys.Contains(k)) current.RemoveAt(i);
        }
        for (int i = 0; i < next.Count; i++)
        {
            if (i >= current.Count) { current.Add(next[i]); continue; }
            if (!ReferenceEquals(current[i], next[i]))
            {
                // The right row is elsewhere — move it. Using Move rather
                // than Remove+Insert keeps WPF's container generator happy.
                var existingIdx = -1;
                for (int j = i + 1; j < current.Count; j++)
                    if (ReferenceEquals(current[j], next[i])) { existingIdx = j; break; }
                if (existingIdx >= 0) current.Move(existingIdx, i);
                else current.Insert(i, next[i]);
            }
        }
    }

    private static void SyncStrings(ObservableCollection<string> current, IReadOnlyList<string> desired)
    {
        if (current.Count == desired.Count)
        {
            bool allMatch = true;
            for (int i = 0; i < current.Count; i++)
                if (!string.Equals(current[i], desired[i], StringComparison.Ordinal)) { allMatch = false; break; }
            if (allMatch) return;
        }
        current.Clear();
        foreach (var s in desired) current.Add(s);
    }

    private static void SyncCommits(ObservableCollection<GitCommit> current, IReadOnlyList<GitCommit> desired)
    {
        // Commits are keyed by hash; if the top-30 hashes match in order,
        // nothing changed.
        if (current.Count == desired.Count)
        {
            bool allMatch = true;
            for (int i = 0; i < current.Count; i++)
                if (current[i].Hash != desired[i].Hash) { allMatch = false; break; }
            if (allMatch) return;
        }
        current.Clear();
        foreach (var c in desired) current.Add(c);
    }

    // ---------- file-level actions ----------

    public async Task StageAsync(GitFileRow row)
    {
        await WithBusy(async () =>
        {
            var res = await GitService.StageAsync(_workingDirectory, row.FullPath);
            if (res.exit != 0) SetError(res.stderr);
        });
        await RefreshAsync();
    }
    public async Task UnstageAsync(GitFileRow row)
    {
        await WithBusy(async () =>
        {
            var res = await GitService.UnstageAsync(_workingDirectory, row.FullPath);
            if (res.exit != 0) SetError(res.stderr);
        });
        await RefreshAsync();
    }
    public async Task DiscardAsync(GitFileRow row)
    {
        await WithBusy(() => GitService.DiscardAsync(_workingDirectory, row.Change));
        await RefreshAsync();
    }
    public async Task StageAllAsync()
    {
        await WithBusy(async () =>
        {
            var res = await GitService.StageAllAsync(_workingDirectory);
            if (res.exit != 0) SetError(res.stderr);
        });
        await RefreshAsync();
    }
    public async Task UnstageAllAsync()
    {
        await WithBusy(async () =>
        {
            var res = await GitService.UnstageAllAsync(_workingDirectory);
            if (res.exit != 0) SetError(res.stderr);
        });
        await RefreshAsync();
    }

    public async Task CommitAsync()
    {
        var msg = (CommitMessage ?? "").Trim();
        if (string.IsNullOrEmpty(msg) || StagedChanges.Count == 0) return;
        await WithBusy(async () =>
        {
            var res = await GitService.CommitAsync(_workingDirectory, msg);
            if (res.exit != 0) SetError(res.stderr);
            else CommitMessage = "";
        });
        await RefreshAsync();
    }

    public async Task AmendAsync()
    {
        await WithBusy(async () =>
        {
            var msg = string.IsNullOrWhiteSpace(CommitMessage) ? null : CommitMessage.Trim();
            var res = await GitService.AmendAsync(_workingDirectory, msg);
            if (res.exit != 0) SetError(res.stderr);
            else if (!string.IsNullOrWhiteSpace(msg)) CommitMessage = "";
        });
        await RefreshAsync();
    }

    // ---------- sync actions ----------

    public async Task FetchAsync() { await WithBusy(async () => ReportGit(await GitService.FetchAsync(_workingDirectory))); await RefreshAsync(); }
    public async Task PullAsync() { await WithBusy(async () => ReportGit(await GitService.PullAsync(_workingDirectory))); await RefreshAsync(); }
    public async Task PushAsync()
    {
        await WithBusy(async () =>
        {
            if (!HasUpstream && !string.IsNullOrEmpty(CurrentBranch))
                ReportGit(await GitService.PushSetUpstreamAsync(_workingDirectory, CurrentBranch));
            else
                ReportGit(await GitService.PushAsync(_workingDirectory));
        });
        await RefreshAsync();
    }

    public async Task CheckoutAsync(string branch)
    {
        await WithBusy(async () =>
        {
            var res = await GitService.CheckoutAsync(_workingDirectory, branch);
            if (res.exit != 0) SetError(res.stderr);
        });
        await RefreshAsync();
    }

    public async Task CreateBranchAsync(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return;
        await WithBusy(async () =>
        {
            var res = await GitService.CreateBranchAsync(_workingDirectory, branch.Trim());
            if (res.exit != 0) SetError(res.stderr);
        });
        await RefreshAsync();
    }

    public async Task StashAsync()
    {
        await WithBusy(async () =>
        {
            var msg = string.IsNullOrWhiteSpace(CommitMessage) ? null : CommitMessage.Trim();
            var res = await GitService.StashAsync(_workingDirectory, msg);
            if (res.exit != 0) SetError(res.stderr);
            else CommitMessage = "";
        });
        await RefreshAsync();
    }

    /// Generate a commit message by piping the staged diff through the
    /// currently-selected CLI tool with a concise prompt. Uses the active
    /// conversation's tool/model so the user's already-configured auth
    /// applies. If nothing is staged we fall back to the full diff so the
    /// user can still get a draft when they haven't staged yet.
    public async Task GenerateMessageWithAIAsync()
    {
        if (IsGeneratingMessage) return;
        IsGeneratingMessage = true;
        try
        {
            var diff = await GitService.StagedDiffAsync(_workingDirectory);
            if (string.IsNullOrWhiteSpace(diff))
            {
                var (_, full, _) = await GitService.RunGitAsync(_workingDirectory, new[] { "diff" });
                diff = full;
            }
            if (string.IsNullOrWhiteSpace(diff))
            {
                SetError("Nothing to describe — no diff.");
                return;
            }

            var settings = _activeConvSettings() ?? new ConversationSettings();
            // Trim to something reasonable — AI tools choke on huge diffs.
            const int maxChars = 40_000;
            if (diff.Length > maxChars) diff = diff[..maxChars] + "\n[… truncated]";

            var prompt =
@"Write a single-line git commit message for the following diff. Rules:
- Under 72 characters.
- Imperative mood (""Add X"", ""Fix Y"", not ""Added"" / ""Fixes"").
- No trailing period.
- No quotes, no code fences, no prefix like ""commit:"".
- Output ONLY the subject line, nothing else.

--- DIFF ---
" + diff;

            var (exit, stdout, stderr) = await RunToolAsync(settings, prompt);
            var text = exit == 0 ? ExtractFirstLine(stdout) : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                SetError(string.IsNullOrWhiteSpace(stderr) ? "AI returned no message." : stderr.Trim());
                return;
            }
            Application.Current?.Dispatcher.Invoke(() => CommitMessage = text);
        }
        catch (Exception ex) { SetError(ex.Message); }
        finally { IsGeneratingMessage = false; }
    }

    /// Invoke the active CLI tool with the prompt on stdin. Reuses the same
    /// executable resolution logic as CliProcessRunner (handles .cmd on Win).
    private async Task<(int exit, string stdout, string stderr)> RunToolAsync(ConversationSettings s, string prompt)
    {
        string exe;
        List<string> args;
        switch (s.Tool)
        {
            case CliTool.Pi:
                exe = "pi";
                args = new List<string> { "--print", "--mode", "text", "--no-session", "--no-context-files", "--no-skills" };
                if (!string.IsNullOrWhiteSpace(s.PiModel)) { args.Add("--model"); args.Add(s.PiModel); }
                break;
            case CliTool.Codex:
                exe = "codex";
                args = new List<string> { "exec", "--dangerously-bypass-approvals-and-sandbox", "--color", "never", "-" };
                if (!string.IsNullOrWhiteSpace(s.CodexModel)) { args.Insert(args.Count - 1, "-m"); args.Insert(args.Count - 1, s.CodexModel); }
                break;
            default: // Claude
                exe = "claude";
                args = new List<string> { "--print", "--dangerously-skip-permissions" };
                if (!string.IsNullOrWhiteSpace(s.ClaudeModel)) { args.Add("--model"); args.Add(s.ClaudeModel); }
                break;
        }

        var resolved = ResolveExecutable(exe);
        if (resolved == null) return (-1, "", $"{exe} not found on PATH");
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };
        var ext = System.IO.Path.GetExtension(resolved).ToLowerInvariant();
        if (ext == ".cmd" || ext == ".bat")
        {
            psi.FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(resolved);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = resolved;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "failed to start");
            await p.StandardInput.WriteAsync(prompt);
            p.StandardInput.Close();
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    private static string? ResolveExecutable(string name)
    {
        try
        {
            var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in pathext)
                {
                    var candidate = System.IO.Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }
        return null;
    }

    private static string ExtractFirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Strip leading blank lines, code fences, quotes
        var lines = s.Split('\n');
        foreach (var raw in lines)
        {
            var t = raw.Trim().TrimEnd('\r');
            if (t.Length == 0) continue;
            if (t.StartsWith("```")) continue;
            // Strip surrounding quotes / backticks
            t = t.Trim('"', '\'', '`', ' ');
            if (t.StartsWith("commit:", StringComparison.OrdinalIgnoreCase)) t = t[7..].Trim();
            return t;
        }
        return "";
    }

    private async Task WithBusy(Func<Task> op)
    {
        IsBusy = true;
        LastError = null;
        try { await op(); }
        catch (Exception ex) { SetError(ex.Message); }
        finally { IsBusy = false; }
    }

    private void SetError(string? msg)
    {
        Application.Current?.Dispatcher.Invoke(() => LastError = string.IsNullOrWhiteSpace(msg) ? null : msg.Trim());
    }

    private void ReportGit((int exit, string stdout, string stderr) r)
    {
        if (r.exit != 0) SetError(r.stderr);
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _gitWatcher?.Dispose();
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
