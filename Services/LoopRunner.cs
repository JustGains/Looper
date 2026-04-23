using System.IO;
using JustCode.Models;

namespace JustCode.Services;

public sealed class LoopRunner
{
    private readonly CliProcessRunner _runner;
    private readonly CodexSessionWatcher _codexWatcher = new();
    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _inactivityTimer;
    private bool _inactivityTripped;
    private readonly object _lock = new();
    private IIterationFormatter? _formatter;
    private string? _capturedSessionId;

    public event EventHandler<string>? Output;
    public event EventHandler<string>? Status;
    public event EventHandler<(int current, int max)>? IterationChanged;
    public event EventHandler<string>? PromptInjected;
    public event EventHandler<string>? SessionCaptured;
    public event EventHandler<(long input, long output, long cached)>? TokenUsageReported;
    public event EventHandler<string>? ToolCallInvoked;
    public event EventHandler<int>? EstimatedInputCharsSet;
    public event EventHandler<int>? EstimatedOutputCharsAppended;
    /// Live stream of the current text/thinking block so the UI can pin the
    /// most recent narrative content above the scrolling tool-call noise.
    public event EventHandler<(string text, bool isThinking)>? PinnedResponseUpdated;

    // --- loop-control signals surfaced to the UI ---
    public event EventHandler<CircuitState>? CircuitStateChanged;
    public event EventHandler<string>? ExitSignalReceived; // payload = STATUS (COMPLETE, BLOCKED, …)
    public event EventHandler<(int open, int closed)>? TaskStatsUpdated;

    public bool IsRunning => _cts != null;

    public LoopRunner(CliProcessRunner runner)
    {
        _runner = runner;
        _runner.OutputLine += (_, line) => HandleLine(line, isError: false);
        _runner.ErrorLine += (_, line) => HandleLine(line, isError: true);
        _codexWatcher.SessionIdCaptured += (_, sid) =>
        {
            _capturedSessionId = sid;
            SessionCaptured?.Invoke(this, sid);
            Output?.Invoke(this, $"[session] codex session={sid}\n");
        };
    }

    private void HandleLine(string line, bool isError)
    {
        ResetInactivity();
        string chunk;
        if (_formatter != null && !isError)
            chunk = _formatter.Format(line);
        else
            chunk = line + "\n";
        if (!string.IsNullOrEmpty(chunk))
            Output?.Invoke(this, chunk);
    }

    public async Task RunAsync(Func<string> promptProvider, Func<string?> tryDequeueQueued, ConversationSettings settings, string workingDirectory, string tasksRelativePath, string? initialSessionId = null, bool chatOnly = false, Func<IReadOnlyList<string>>? enabledSkillPathsProvider = null, bool forceContinueSession = false)
    {
        if (IsRunning) return;

        var cts = new CancellationTokenSource();
        lock (_lock) _cts = cts;
        _capturedSessionId = initialSessionId;

        if (settings.Tool == CliTool.Codex && settings.KeepContext)
            _codexWatcher.Start();

        // Loop-level state (reset per Start).
        var git = new GitProgressTracker(workingDirectory);
        var circuit = new ProgressCircuitBreaker();
        CircuitStateChanged?.Invoke(this, circuit.State);
        var carryGuidance = new List<string>(); // corrective notes for the next iteration

        try
        {
            int effectiveMax = chatOnly
                ? 0
                : (settings.RalphEnabled ? Math.Max(1, settings.MaxIterations) : 1);
            int iter = 1;
            while (!cts.IsCancellationRequested)
            {
                // If the circuit is OPEN we halt the scheduled loop, but still
                // allow queued chat turns (manual interjection).
                var queuedPrompt = tryDequeueQueued();
                bool isQueued = queuedPrompt != null;
                if (!isQueued && circuit.State == CircuitState.Open)
                {
                    Output?.Invoke(this, "[justcode] circuit open — progress stalled for "
                        + $"{circuit.ConsecutiveNoProgress} iterations. Pausing the loop. "
                        + "Fix the blockers manually (or edit the prompt and press Start) "
                        + "to resume.\n");
                    Status?.Invoke(this, "Circuit open");
                    break;
                }

                var stats = ReadTaskStats(workingDirectory, tasksRelativePath);
                TaskStatsUpdated?.Invoke(this, (stats.Open, stats.Closed));

                string currentPrompt;
                string wrapped;
                if (isQueued)
                {
                    currentPrompt = queuedPrompt!;
                    wrapped = currentPrompt;
                }
                else
                {
                    if (iter > effectiveMax) break;
                    currentPrompt = promptProvider();
                    wrapped = BuildWrappedPrompt(
                        currentPrompt, iter, effectiveMax, tasksRelativePath,
                        stats, carryGuidance, circuit.State, settings);
                }
                carryGuidance.Clear();

                _formatter = settings.Tool switch
                {
                    CliTool.ClaudeCode => new StreamJsonFormatter(),
                    CliTool.Pi => new PiJsonFormatter(),
                    _ => null,
                };
                if (_formatter != null)
                {
                    _formatter.SessionIdCaptured += (_, sid) =>
                    {
                        _capturedSessionId = sid;
                        SessionCaptured?.Invoke(this, sid);
                    };
                    _formatter.TokenUsageReported += (_, u) => TokenUsageReported?.Invoke(this, u);
                    _formatter.ToolCallInvoked += (_, name) => ToolCallInvoked?.Invoke(this, name);
                    _formatter.EstimatedOutputCharsAppended += (_, n) =>
                        EstimatedOutputCharsAppended?.Invoke(this, n);
                    _formatter.NonToolBlockUpdated += (_, p) => PinnedResponseUpdated?.Invoke(this, p);
                }

                EstimatedInputCharsSet?.Invoke(this, wrapped.Length);

                // Snapshot working tree state BEFORE the CLI runs, so we can
                // detect progress afterwards. Queued turns don't participate
                // in stuck-loop detection (they're manual interjections).
                if (!isQueued) git.Snapshot();

                var body = string.IsNullOrWhiteSpace(currentPrompt) ? "<empty>" : currentPrompt;
                if (isQueued)
                {
                    Output?.Invoke(this, $"\n── queued message ──\n{body}\n");
                }
                else
                {
                    IterationChanged?.Invoke(this, (iter, effectiveMax));
                    PromptInjected?.Invoke(this, currentPrompt);
                    Output?.Invoke(this, $"\n── iteration {iter}/{effectiveMax} ──\n{body}\n");
                }
                Status?.Invoke(this, "Running");

                _inactivityTripped = false;
                StartInactivity(settings.TimeoutSeconds);
                int exitCode;
                // Pending fork (one-shot): iter 1 forks from the parent's
                // session id. Once the run captures a new session id, the
                // VM clears PendingForkFromSessionId and subsequent iters
                // behave like a normal resume.
                bool doFork = !string.IsNullOrEmpty(settings.PendingForkFromSessionId)
                              && string.IsNullOrEmpty(_capturedSessionId);
                var forkFromId = doFork ? settings.PendingForkFromSessionId : null;
                // Only resume when we have *this conversation's* own captured
                // session id. Falling back to the CLI's "most recent session"
                // (via `--continue` / `exec resume --last`) would silently hop
                // into another conversation's session — so never do that.
                var continueSession = doFork
                    || (forceContinueSession && !string.IsNullOrEmpty(_capturedSessionId))
                    || (settings.KeepContext && !string.IsNullOrEmpty(_capturedSessionId));
                var runSessionId = forkFromId ?? _capturedSessionId;
                try
                {
                    // Only Pi uses --skill; other tools receive null so the
                    // runner keeps its existing arg list untouched.
                    var skillPaths = (settings.Tool == CliTool.Pi)
                        ? enabledSkillPathsProvider?.Invoke()
                        : null;
                    exitCode = await _runner.RunAsync(settings, workingDirectory, wrapped,
                        continueSession, runSessionId, cts.Token, skillPaths, fork: doFork);
                }
                finally
                {
                    StopInactivity();
                }

                if (cts.IsCancellationRequested && !_inactivityTripped)
                {
                    Status?.Invoke(this, "Stopped");
                    return;
                }

                if (_inactivityTripped)
                {
                    var label = isQueued ? "queued message" : $"iteration {iter}";
                    Output?.Invoke(this, $"[justcode] no output for {settings.TimeoutSeconds}s — killed; retrying {label}\n");
                    Status?.Invoke(this, "Restarting");
                    continue;
                }

                // Re-read task state after the CLI turn so exit gating uses
                // the latest checkbox counts, not the pre-iteration snapshot.
                var postStats = ReadTaskStats(workingDirectory, tasksRelativePath);
                TaskStatsUpdated?.Invoke(this, (postStats.Open, postStats.Closed));

                // --- Post-iteration signal collection ---
                var f = _formatter;
                bool exitRequested = false;
                bool fatalError = false;
                string? ignoredExitReason = null;
                if (f != null)
                {
                    if (f.IterationExitSignal)
                    {
                        var status = f.IterationStatus?.ToUpperInvariant();
                        if (string.Equals(status, "COMPLETE", StringComparison.Ordinal) && postStats.Open == 0)
                        {
                            exitRequested = true;
                            ExitSignalReceived?.Invoke(this, "COMPLETE");
                        }
                        else
                        {
                            ignoredExitReason = status switch
                            {
                                "COMPLETE" => $"tasks.md still has {postStats.Open} open task{(postStats.Open == 1 ? "" : "s")}",
                                null or "" => "the final RALPH_STATUS block did not include STATUS: COMPLETE",
                                _ => $"the final RALPH_STATUS block reported STATUS={status} instead of COMPLETE"
                            };
                            carryGuidance.Add("The previous iteration requested EXIT_SIGNAL:true too early. Only exit when the final RALPH_STATUS block says STATUS: COMPLETE and `tasks.md` has zero open checkboxes.");
                        }
                    }
                    if (f.IterationFatalError)
                    {
                        fatalError = true;
                    }
                    if (f.IterationAskedQuestion)
                        carryGuidance.Add("The previous iteration asked a clarifying question. Do NOT ask questions — make reasonable decisions and proceed. If a decision is truly ambiguous, document the assumption in `tasks.md` and continue.");
                    if (f.IterationToolErrors >= 2)
                        carryGuidance.Add($"The previous iteration had {f.IterationToolErrors} tool errors. Read the error output carefully, diagnose the root cause, and verify your fix before continuing.");
                }

                // Git progress → circuit breaker. Skipped for queued turns.
                if (!isQueued)
                {
                    if (git.IsGitRepo && !git.HasProgressed())
                    {
                        circuit.RecordNoProgress();
                        Output?.Invoke(this, $"[justcode] no git progress this iteration (streak: {circuit.ConsecutiveNoProgress}).\n");
                    }
                    else
                    {
                        if (circuit.ConsecutiveNoProgress > 0)
                            Output?.Invoke(this, "[justcode] progress detected — circuit reset.\n");
                        circuit.RecordProgress();
                    }
                    CircuitStateChanged?.Invoke(this, circuit.State);
                    if (circuit.State == CircuitState.HalfOpen)
                        carryGuidance.Add("Progress has stalled for two iterations (no file changes). This iteration must land concrete edits: inspect what you've been planning vs. what actually changed, and make real file modifications.");
                }

                if (isQueued)
                    Output?.Invoke(this, $"[justcode] queued message exited with code {exitCode}\n");
                else
                    Output?.Invoke(this, $"[justcode] iteration {iter}/{effectiveMax} exited with code {exitCode}\n");

                // Fatal provider error. Some of these (image-dimension limit,
                // invalid/missing session id, context overflow) can be cleared
                // by starting a fresh session — the offending content only
                // lives in the old session's history. For those we clear the
                // captured id, inject a recovery note, and continue the loop
                // in a brand-new session. For everything else (auth, rate
                // limit) a fresh session won't help, so we stop.
                if (fatalError)
                {
                    var msg = f?.IterationFatalErrorMessage ?? "(no error message)";
                    if (StreamJsonFormatter.IsRecoverableByFreshSession(msg) && !isQueued)
                    {
                        var deadId = _capturedSessionId;
                        _capturedSessionId = null; // next iter starts fresh
                        // Tell the VM to clear its persisted session id too —
                        // fire the SessionCaptured event with empty to signal
                        // reset (the VM accepts null via its clear path).
                        SessionCaptured?.Invoke(this, "");
                        Output?.Invoke(this, $"[justcode] provider rejected this turn: {msg}\n");
                        Output?.Invoke(this, "[justcode] starting a fresh session and reinjecting the most recent task context…\n");
                        Status?.Invoke(this, "Recovering");
                        carryGuidance.Add(
                            "Recovery mode: the previous session was interrupted by a provider error "
                            + $"(\"{Shorten(msg, 200)}\"). You are now in a BRAND-NEW session with no memory of the "
                            + "previous turns. The last task-summary block above is your only context from that run. "
                            + "Do NOT re-ask clarifying questions and do NOT restart the overall task from scratch — "
                            + "re-read any files you need, consult the task list, and continue the user's work from "
                            + "where the summary left off.");
                        // Don't increment iter — retry this slot in the fresh
                        // session. If the retry also fatals, the circuit will
                        // eventually trip (consecutive no-progress).
                        if (!isQueued && circuit.State == CircuitState.Open)
                        {
                            Output?.Invoke(this, "[justcode] circuit already open — stopping after recovery attempt.\n");
                            Status?.Invoke(this, "Circuit open");
                            return;
                        }
                        continue;
                    }
                    Output?.Invoke(this, $"[justcode] stopping loop — provider rejected the turn: {msg}\n");
                    Status?.Invoke(this, "Error");
                    return;
                }

                // Early exit on explicit model signal.
                if (exitRequested)
                {
                    Output?.Invoke(this, $"[justcode] model reported EXIT_SIGNAL (status={f!.IterationStatus ?? "?"}, open tasks={postStats.Open}) — ending loop.\n");
                    Status?.Invoke(this, "Completed");
                    return;
                }
                if (!string.IsNullOrEmpty(ignoredExitReason))
                    Output?.Invoke(this, $"[justcode] ignoring EXIT_SIGNAL — {ignoredExitReason}.\n");

                if (!isQueued) iter++;
            }

            Status?.Invoke(this, cts.IsCancellationRequested ? "Stopped" : "Completed");
        }
        finally
        {
            lock (_lock) _cts = null;
            StopInactivity();
            _codexWatcher.Stop();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? c;
        lock (_lock) c = _cts;
        if (c != null)
        {
            Status?.Invoke(this, "Killing");
            c.Cancel();
            _runner.KillCurrent();
        }
    }

    private const double ThinkingTimeoutFactor = 3.0;
    private double _baseInactivityMs;

    private void StartInactivity(int timeoutSec)
    {
        StopInactivity();
        _baseInactivityMs = Math.Max(1, timeoutSec) * 1000.0;
        _inactivityTimer = new System.Timers.Timer(_baseInactivityMs) { AutoReset = false };
        _inactivityTimer.Elapsed += (_, _) =>
        {
            _inactivityTripped = true;
            _runner.KillCurrent();
        };
        _inactivityTimer.Start();
    }

    private void ResetInactivity()
    {
        var t = _inactivityTimer;
        if (t == null) return;
        t.Stop();
        var factor = _formatter?.IsInThinking == true ? ThinkingTimeoutFactor : 1.0;
        t.Interval = _baseInactivityMs * factor;
        t.Start();
    }

    private void StopInactivity()
    {
        var t = _inactivityTimer;
        _inactivityTimer = null;
        t?.Stop();
        t?.Dispose();
    }

    private static string Shorten(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    private static TaskStats ReadTaskStats(string workingDirectory, string tasksRelativePath)
    {
        try
        {
            var abs = Path.Combine(workingDirectory, tasksRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = File.Exists(abs) ? File.ReadAllText(abs) : "";
            return TasksMarkdownAnalyzer.Analyze(content);
        }
        catch
        {
            return new TaskStats(0, 0, null);
        }
    }

    private static string BuildWrappedPrompt(
        string userPrompt,
        int iter,
        int maxIter,
        string tasksRelativePath,
        TaskStats stats,
        IReadOnlyList<string> guidance,
        CircuitState circuit,
        ConversationSettings settings)
    {
        var tp = string.IsNullOrWhiteSpace(tasksRelativePath) ? ".looper/tasks.md" : tasksRelativePath.Replace('\\', '/');

        // Loop context banner — gives the model awareness of where it is.
        var ctx = new System.Text.StringBuilder();
        ctx.Append("LOOP ").Append(iter).Append('/').Append(maxIter);
        ctx.Append(" · ").Append(stats.Open).Append(" open task").Append(stats.Open == 1 ? "" : "s");
        if (stats.Closed > 0) ctx.Append(" · ").Append(stats.Closed).Append(" done");
        if (circuit != CircuitState.Closed)
            ctx.Append(" · CIRCUIT ").Append(circuit == CircuitState.Open ? "OPEN" : "HALF-OPEN");

        var guidanceBlock = guidance.Count > 0
            ? "\n--- CORRECTIVE GUIDANCE (from prior iteration) ---\n- " + string.Join("\n- ", guidance) + "\n"
            : "";

        var summaryBlock = string.IsNullOrWhiteSpace(stats.LastSummary)
            ? ""
            : "\n--- LAST SESSION SUMMARY (from `" + tp + "`) ---\n" + stats.LastSummary + "\n";

        return $@"{ctx}

Track work in `{tp}` (relative to cwd). Create it if missing. Use GitHub checkbox syntax (`- [ ]` / `- [x]`). Append new tasks as you discover them, tick boxes in place as you finish. Do not rewrite the file from scratch.

In this run, do as much useful work as you can — complete as many tasks as possible before stopping. Do NOT stop after a single task. Keep going until the list is done, you are blocked, or there is nothing sensible left to do.

Before you stop (whether the list is done, you are blocked, or you've reached a sensible stopping point), you MUST append a completion summary to `{tp}` under a new `### <UTC timestamp> — session summary` heading. Use 1-line bullets covering: what you actually completed this session, any decisions worth remembering, and any blockers or follow-ups you're leaving for the next session. Keep prior summaries below; never overwrite them.

At the very end of your response — AFTER the summary has been appended to `{tp}` — emit a single status block in plain text, exactly like this (no backticks, no code fence):

---RALPH_STATUS---
EXIT_SIGNAL: <true|false>
STATUS: <COMPLETE|IN_PROGRESS|BLOCKED>
---RALPH_STATUS---

Set EXIT_SIGNAL to true **only** when the user's goal is fully achieved, `STATUS: COMPLETE`, and `{tp}` has zero open checkboxes. If any tasks remain open, set `EXIT_SIGNAL: false`. Otherwise set it to false so the loop continues.{guidanceBlock}{summaryBlock}
--- USER PROMPT ---
{userPrompt}
";
    }
}
