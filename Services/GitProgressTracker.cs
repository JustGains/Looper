using System.Diagnostics;
using System.Text;

namespace JustCode.Services;

/// Tracks whether anything visibly changed in the working tree between
/// snapshots. Used by `LoopRunner` to detect stuck loops (no progress across
/// several iterations ⇒ open the circuit breaker and pause the run).
public sealed class GitProgressTracker
{
    private readonly string _workingDirectory;
    private string? _snapshotSha;
    private string _snapshotStatus = "";

    public GitProgressTracker(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    /// True if a git HEAD was resolvable on construction or last snapshot.
    /// When false, `HasProgressed` reports true — we can't tell, so we don't
    /// falsely accuse the loop of being stuck.
    public bool IsGitRepo { get; private set; }

    public void Snapshot()
    {
        _snapshotSha = RunGit("rev-parse HEAD");
        _snapshotStatus = RunGit("status --porcelain=v1") ?? "";
        IsGitRepo = _snapshotSha is not null;
    }

    /// Returns true if anything measurable changed since the last Snapshot:
    /// a new commit on HEAD, or a different dirty-file set (including staged).
    public bool HasProgressed()
    {
        if (!IsGitRepo) return true; // unknown ⇒ don't punish the loop
        var sha = RunGit("rev-parse HEAD");
        if (sha != _snapshotSha) return true;
        var status = RunGit("status --porcelain=v1") ?? "";
        return status != _snapshotStatus;
    }

    private string? RunGit(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // Split args safely (these inputs are constants controlled by us).
            foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? sb.ToString().TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }
}

public enum CircuitState { Closed, HalfOpen, Open }

/// Simple counter-based circuit that trips after N consecutive no-progress
/// iterations. Separate from `GitProgressTracker` so it can also be advanced
/// by manual signals (e.g. unrecoverable tool errors) later.
public sealed class ProgressCircuitBreaker
{
    private int _consecutiveNoProgress;

    public const int HalfOpenThreshold = 2;
    public const int OpenThreshold = 3;

    public CircuitState State { get; private set; } = CircuitState.Closed;
    public int ConsecutiveNoProgress => _consecutiveNoProgress;

    public void RecordProgress()
    {
        _consecutiveNoProgress = 0;
        State = CircuitState.Closed;
    }

    public void RecordNoProgress()
    {
        _consecutiveNoProgress++;
        State = _consecutiveNoProgress switch
        {
            >= OpenThreshold => CircuitState.Open,
            >= HalfOpenThreshold => CircuitState.HalfOpen,
            _ => CircuitState.Closed,
        };
    }

    public void Reset()
    {
        _consecutiveNoProgress = 0;
        State = CircuitState.Closed;
    }
}
