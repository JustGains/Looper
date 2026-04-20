using Looper.Models;

namespace Looper.Services;

public sealed class LoopRunner
{
    private readonly CliProcessRunner _runner;
    private readonly CodexSessionWatcher _codexWatcher = new();
    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _inactivityTimer;
    private bool _inactivityTripped;
    private readonly object _lock = new();
    private StreamJsonFormatter? _formatter;
    private string? _capturedSessionId;

    public event EventHandler<string>? Output;
    public event EventHandler<string>? Status;
    public event EventHandler<(int current, int max)>? IterationChanged;
    public event EventHandler<string>? PromptInjected;

    public bool IsRunning => _cts != null;

    public LoopRunner(CliProcessRunner runner)
    {
        _runner = runner;
        _runner.OutputLine += (_, line) => HandleLine(line, isError: false);
        _runner.ErrorLine += (_, line) => HandleLine(line, isError: true);
        _codexWatcher.SessionIdCaptured += (_, sid) =>
        {
            _capturedSessionId = sid;
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

    public async Task RunAsync(Func<string> promptProvider, LoopSettings settings, string workingDirectory)
    {
        if (IsRunning) return;

        var cts = new CancellationTokenSource();
        lock (_lock) _cts = cts;
        _capturedSessionId = null;

        // For Codex, watch ~/.codex/sessions/ for new session files so we can
        // pin the session id and use `exec resume <id>` on iteration 2+.
        if (settings.Tool == CliTool.Codex && settings.KeepContext)
            _codexWatcher.Start();

        try
        {
            int effectiveMax = settings.RalphEnabled ? Math.Max(1, settings.MaxIterations) : 1;
            int iter = 1;
            while (iter <= effectiveMax && !cts.IsCancellationRequested)
            {
                var currentPrompt = promptProvider();
                var wrapped = BuildWrappedPrompt(currentPrompt, effectiveMax);

                _formatter = settings.Tool == CliTool.ClaudeCode ? new StreamJsonFormatter() : null;
                if (_formatter != null)
                {
                    _formatter.SessionIdCaptured += (_, sid) => _capturedSessionId = sid;
                }

                IterationChanged?.Invoke(this, (iter, effectiveMax));
                PromptInjected?.Invoke(this, currentPrompt);
                Output?.Invoke(this, $"\n── iteration {iter}/{settings.MaxIterations} ──\n");
                Status?.Invoke(this, "Running");

                _inactivityTripped = false;
                StartInactivity(settings.TimeoutSeconds);
                int exitCode;
                var continueSession = settings.KeepContext && iter > 1;
                try
                {
                    exitCode = await _runner.RunAsync(settings, workingDirectory, wrapped, continueSession, _capturedSessionId, cts.Token);
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
                    Output?.Invoke(this, $"[looper] no output for {settings.TimeoutSeconds}s — killed; retrying iteration {iter}\n");
                    Status?.Invoke(this, "Restarting");
                    continue;
                }

                Output?.Invoke(this, $"[looper] iteration {iter}/{effectiveMax} exited with code {exitCode}\n");
                iter++;
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

    private void StartInactivity(int timeoutSec)
    {
        StopInactivity();
        _inactivityTimer = new System.Timers.Timer(Math.Max(1, timeoutSec) * 1000.0) { AutoReset = false };
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
        if (t != null)
        {
            t.Stop();
            t.Start();
        }
    }

    private void StopInactivity()
    {
        var t = _inactivityTimer;
        _inactivityTimer = null;
        t?.Stop();
        t?.Dispose();
    }

    private static string BuildWrappedPrompt(string userPrompt, int maxIter) =>
        $@"Track work in `.looper/tasks.md` (relative to cwd). Create it if missing. Use GitHub checkbox syntax (`- [ ]` / `- [x]`). Append new tasks as you discover them, tick boxes in place as you finish. Do not rewrite the file from scratch.

In this run, do as much useful work as you can — complete as many tasks as possible before stopping. Do NOT stop after a single task. Keep going until the list is done, you are blocked, or there is nothing sensible left to do.

--- USER PROMPT ---
{userPrompt}
";
}
