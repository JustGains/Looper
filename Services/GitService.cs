using System.Diagnostics;
using System.IO;
using System.Text;

namespace JustCode.Services;

public enum GitChangeKind
{
    Untracked,  // ??
    Modified,   // M
    Added,      // A
    Deleted,    // D
    Renamed,    // R
    Copied,     // C
    TypeChanged, // T
    Conflicted, // U / UU / AA / DD …
}

public sealed record GitFileChange(
    string Path,
    GitChangeKind IndexKind,   // state in the index (staged)
    GitChangeKind WorkingKind, // state in the working tree (unstaged)
    bool HasStaged,
    bool HasUnstaged,
    string? OriginalPath);     // for renames

public sealed record GitCommit(
    string Hash,
    string ShortHash,
    string Author,
    string Subject,
    DateTime DateUtc);

public sealed record GitStatus(
    string? CurrentBranch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool Detached,
    IReadOnlyList<GitFileChange> Changes);

/// Thin wrapper around the `git` CLI. Every operation spawns a subprocess
/// with UTF-8 stdout; output is captured synchronously via async Process.
/// Designed for the sidebar UI, not for scripting — nothing here holds a
/// long-lived repo handle.
public static class GitService
{
    public static bool IsRepo(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return false;
        return Directory.Exists(Path.Combine(workingDirectory, ".git"))
            || File.Exists(Path.Combine(workingDirectory, ".git")); // worktrees: .git is a file
    }

    /// Run `git status --porcelain=v2 --branch` and parse into a GitStatus.
    public static async Task<GitStatus> GetStatusAsync(string workingDirectory, CancellationToken ct = default)
    {
        var (exit, stdout, _) = await RunGitAsync(workingDirectory,
            new[] { "status", "--porcelain=v2", "--branch" }, ct);
        if (exit != 0) return new GitStatus(null, null, 0, 0, false, Array.Empty<GitFileChange>());

        string? branch = null;
        string? upstream = null;
        int ahead = 0, behind = 0;
        bool detached = false;
        var files = new List<GitFileChange>();

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Branch headers: "# branch.oid <hash>", "# branch.head <name>",
            // "# branch.upstream <u>", "# branch.ab +N -M"
            if (line.StartsWith("# branch.head"))
            {
                var h = line["# branch.head".Length..].Trim();
                if (h == "(detached)") detached = true;
                else branch = h;
                continue;
            }
            if (line.StartsWith("# branch.upstream"))
            {
                upstream = line["# branch.upstream".Length..].Trim();
                continue;
            }
            if (line.StartsWith("# branch.ab"))
            {
                var rest = line["# branch.ab".Length..].Trim();
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (p.StartsWith('+') && int.TryParse(p[1..], out var a)) ahead = a;
                    else if (p.StartsWith('-') && int.TryParse(p[1..], out var b)) behind = b;
                }
                continue;
            }
            if (line.StartsWith('#')) continue; // other header

            // Changed entries:
            //   "1 XY sub <mH> <mI> <mW> <hH> <hI> <path>"   (ordinary)
            //   "2 XY sub <mH> <mI> <mW> <hH> <hI> <X><score> <path>\t<orig>" (rename/copy)
            //   "u XY sub <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>" (unmerged)
            //   "? <path>"                                                   (untracked)
            //   "! <path>"                                                   (ignored)
            if (line.StartsWith("? "))
            {
                files.Add(new GitFileChange(line[2..], GitChangeKind.Untracked, GitChangeKind.Untracked,
                    HasStaged: false, HasUnstaged: true, OriginalPath: null));
                continue;
            }
            if (line.StartsWith("! ")) continue;

            if (line.StartsWith("1 ") || line.StartsWith("2 ") || line.StartsWith("u "))
            {
                var parts = line.Split(' ', 9, StringSplitOptions.None);
                if (parts.Length < 9) continue;
                var xy = parts[1]; // two chars: X=index, Y=worktree
                var pathField = parts[8];
                string? origPath = null;
                string path = pathField;
                if (line.StartsWith("2 "))
                {
                    // parts[8] is "<score>\t<path>\t<origPath>" or "<score> <path>\t<origPath>"
                    // For rename/copy the last two fields are TAB-separated: path\torigPath.
                    var tabIdx = pathField.IndexOf('\t');
                    if (tabIdx > 0)
                    {
                        // "<score> <path>\t<orig>" → split off the score
                        var spaceIdx = pathField.IndexOf(' ');
                        var afterScore = spaceIdx > 0 && spaceIdx < tabIdx ? pathField[(spaceIdx + 1)..] : pathField;
                        var tab2 = afterScore.IndexOf('\t');
                        if (tab2 > 0)
                        {
                            path = afterScore[..tab2];
                            origPath = afterScore[(tab2 + 1)..];
                        }
                    }
                }

                var indexKind = ParseCode(xy.Length > 0 ? xy[0] : ' ');
                var workKind = ParseCode(xy.Length > 1 ? xy[1] : ' ');
                files.Add(new GitFileChange(path, indexKind, workKind,
                    HasStaged: xy.Length > 0 && xy[0] != '.' && xy[0] != ' ',
                    HasUnstaged: xy.Length > 1 && xy[1] != '.' && xy[1] != ' ',
                    OriginalPath: origPath));
            }
        }

        return new GitStatus(branch, upstream, ahead, behind, detached, files);
    }

    public static async Task<IReadOnlyList<string>> GetBranchesAsync(string workingDirectory, CancellationToken ct = default)
    {
        var (exit, stdout, _) = await RunGitAsync(workingDirectory,
            new[] { "branch", "--list", "--format=%(refname:short)" }, ct);
        if (exit != 0) return Array.Empty<string>();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    public static async Task<IReadOnlyList<GitCommit>> GetRecentCommitsAsync(
        string workingDirectory, int count = 30, CancellationToken ct = default)
    {
        // %x1f = unit separator; safer than any visible delimiter
        var fmt = "%H%x1f%h%x1f%an%x1f%cI%x1f%s";
        var (exit, stdout, _) = await RunGitAsync(workingDirectory,
            new[] { "log", $"-n{count}", "--pretty=format:" + fmt }, ct);
        if (exit != 0) return Array.Empty<GitCommit>();
        var commits = new List<GitCommit>();
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length == 0) continue;
            var parts = t.Split('\x1f');
            if (parts.Length < 5) continue;
            DateTime dt = DateTime.UtcNow;
            DateTime.TryParse(parts[3], null, System.Globalization.DateTimeStyles.RoundtripKind, out dt);
            commits.Add(new GitCommit(parts[0], parts[1], parts[2], parts[4], dt.ToUniversalTime()));
        }
        return commits;
    }

    public static Task<(int exit, string stdout, string stderr)> StageAsync(string wd, string path, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "add", "--", path }, ct);

    public static Task<(int exit, string stdout, string stderr)> StageAllAsync(string wd, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "add", "-A" }, ct);

    public static Task<(int exit, string stdout, string stderr)> UnstageAsync(string wd, string path, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "reset", "HEAD", "--", path }, ct);

    public static Task<(int exit, string stdout, string stderr)> UnstageAllAsync(string wd, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "reset", "HEAD" }, ct);

    /// Discards working-tree changes for a tracked file, or deletes the file
    /// if it's untracked. Destructive — callers must confirm.
    public static async Task DiscardAsync(string wd, GitFileChange change, CancellationToken ct = default)
    {
        if (change.IndexKind == GitChangeKind.Untracked)
        {
            try { File.Delete(Path.Combine(wd, change.Path)); } catch { }
            return;
        }
        await RunGitAsync(wd, new[] { "checkout", "--", change.Path }, ct);
    }

    public static Task<(int exit, string stdout, string stderr)> CommitAsync(string wd, string message, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "commit", "-m", message }, ct);

    /// Amend the most recent commit, reusing its message unless one is provided.
    public static Task<(int exit, string stdout, string stderr)> AmendAsync(string wd, string? message, CancellationToken ct = default)
    {
        var args = new List<string> { "commit", "--amend" };
        if (string.IsNullOrEmpty(message)) args.Add("--no-edit");
        else { args.Add("-m"); args.Add(message); }
        return RunGitAsync(wd, args.ToArray(), ct);
    }

    public static Task<(int exit, string stdout, string stderr)> FetchAsync(string wd, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "fetch", "--prune" }, ct);

    public static Task<(int exit, string stdout, string stderr)> PullAsync(string wd, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "pull", "--ff-only" }, ct);

    public static Task<(int exit, string stdout, string stderr)> PushAsync(string wd, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "push" }, ct);

    public static Task<(int exit, string stdout, string stderr)> PushSetUpstreamAsync(string wd, string branch, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "push", "-u", "origin", branch }, ct);

    public static Task<(int exit, string stdout, string stderr)> CheckoutAsync(string wd, string branch, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "checkout", branch }, ct);

    public static Task<(int exit, string stdout, string stderr)> CreateBranchAsync(string wd, string branch, CancellationToken ct = default)
        => RunGitAsync(wd, new[] { "checkout", "-b", branch }, ct);

    public static Task<(int exit, string stdout, string stderr)> StashAsync(string wd, string? message, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "push" };
        if (!string.IsNullOrEmpty(message)) { args.Add("-m"); args.Add(message); }
        return RunGitAsync(wd, args.ToArray(), ct);
    }

    /// Returns the working-tree diff for a single file, combined view if it
    /// appears both staged and unstaged. Used by the diff pane.
    public static async Task<string> DiffAsync(string wd, string path, bool staged, CancellationToken ct = default)
    {
        var args = staged
            ? new[] { "diff", "--cached", "--", path }
            : new[] { "diff", "--", path };
        var (_, stdout, _) = await RunGitAsync(wd, args, ct);
        return stdout;
    }

    /// Staged diff for every change about to be committed. Used by the AI
    /// commit-message generator — we feed this to the active tool with a
    /// concise prompt.
    public static async Task<string> StagedDiffAsync(string wd, CancellationToken ct = default)
    {
        var (_, stdout, _) = await RunGitAsync(wd, new[] { "diff", "--cached" }, ct);
        return stdout;
    }

    /// Generic runner. Returns (exit, stdout, stderr). `git` must be on PATH.
    public static async Task<(int exit, string stdout, string stderr)> RunGitAsync(
        string workingDirectory, string[] args, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "failed to start git");
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static GitChangeKind ParseCode(char c) => c switch
    {
        'M' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Conflicted,
        _ => GitChangeKind.Modified,
    };
}
