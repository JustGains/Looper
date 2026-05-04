using System.Diagnostics;
using System.IO;
using System.Text;
using JustCode.Models;

namespace JustCode.Services;

public sealed class CliProcessRunner
{
    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;

    private Process? _process;
    private readonly object _lock = new();

    public async Task<int> RunAsync(ConversationSettings settings, string workingDirectory, string prompt, bool continueSession, string? sessionId, CancellationToken ct, IReadOnlyList<string>? skillPaths = null, bool fork = false)
    {
        var (fileName, args) = BuildCommand(settings, continueSession, sessionId, skillPaths, fork);
        var resolved = ResolveExecutable(fileName);
        if (resolved == null)
        {
            ErrorLine?.Invoke(this, $"[justcode] executable not found on PATH: {fileName}");
            return -1;
        }

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (ext == ".cmd" || ext == ".bat")
        {
            psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = resolved;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputLine?.Invoke(this, e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) ErrorLine?.Invoke(this, e.Data);
        };

        lock (_lock) _process = proc;

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();

            using var reg = ct.Register(() => KillTree(proc));
            await proc.WaitForExitAsync(CancellationToken.None);
            return proc.ExitCode;
        }
        finally
        {
            lock (_lock) _process = null;
            proc.Dispose();
        }
    }

    public void KillCurrent()
    {
        Process? p;
        lock (_lock) p = _process;
        if (p != null) KillTree(p);
    }

    private static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string? ResolveExecutable(string name)
    {
        var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in pathext)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            try
            {
                var bare = Path.Combine(dir.Trim(), name);
                if (File.Exists(bare) && Path.GetExtension(bare).Length > 0) return bare;
            }
            catch { }
        }
        return null;
    }

    private static (string fileName, string[] args) BuildCommand(ConversationSettings s, bool continueSession, string? sessionId, IReadOnlyList<string>? skillPaths, bool fork)
    {
        switch (s.Tool)
        {
            case CliTool.ClaudeCode:
            {
                var a = new List<string>
                {
                    "--print",
                    "--dangerously-skip-permissions",
                    "--verbose",
                    "--output-format", "stream-json",
                    "--include-partial-messages",
                    "--thinking-display", "summarized",
                };
                // continueSession is only true when we have our own session id
                // (LoopRunner guarantees this); --continue without an id is a
                // footgun that resumes another conversation's session.
                if (continueSession && !string.IsNullOrEmpty(sessionId))
                {
                    a.Add("--resume");
                    a.Add(sessionId);
                    // `--fork-session`: resume at this point but emit a new
                    // session id so the parent is untouched. Claude supports
                    // this natively — one flag, done.
                    if (fork) a.Add("--fork-session");
                }
                if (!string.IsNullOrWhiteSpace(s.ClaudeModel))
                {
                    a.Add("--model");
                    a.Add(s.ClaudeModel);
                }
                if (!string.IsNullOrWhiteSpace(s.ClaudeEffort))
                {
                    a.Add("--effort");
                    a.Add(s.ClaudeEffort);
                }
                return ("claude", a.ToArray());
            }
            case CliTool.Codex:
            {
                var a = new List<string> { "exec" };
                // Codex has `codex fork` only as an INTERACTIVE command; there
                // is no `codex exec fork`. To fork non-interactively we copy
                // the session file to a new uuid and resume the copy, which
                // isolates the fork from the parent's session file.
                var effectiveSessionId = sessionId;
                if (fork && !string.IsNullOrEmpty(sessionId))
                {
                    var forked = ForkCodexSessionFile(sessionId);
                    if (!string.IsNullOrEmpty(forked)) effectiveSessionId = forked;
                }
                if (continueSession && !string.IsNullOrEmpty(effectiveSessionId))
                {
                    // `codex exec resume <id>` — session id is the only way to
                    // isolate between conversations. Never use `--last`, which
                    // resumes the CLI's global most-recent (= another conv).
                    a.Add("resume");
                    a.Add("--dangerously-bypass-approvals-and-sandbox");
                    a.Add(effectiveSessionId);
                }
                else
                {
                    a.Add("--dangerously-bypass-approvals-and-sandbox");
                    a.Add("--color");
                    a.Add("never");
                    if (!string.IsNullOrWhiteSpace(s.CodexModel))
                    {
                        a.Add("-m");
                        a.Add(s.CodexModel);
                    }
                    if (!string.IsNullOrWhiteSpace(s.CodexEffort))
                    {
                        a.Add("-c");
                        a.Add($"model_reasoning_effort=\"{s.CodexEffort}\"");
                    }
                }
                a.Add("-");
                return ("codex", a.ToArray());
            }
            case CliTool.Pi:
            {
                // pi accepts prompt via stdin (piped) in --print mode.
                var a = new List<string>
                {
                    "--print",
                    "--mode", "json",
                    "--no-context-files", // we manage our own context via the wrapper
                };
                // Fork beats resume: --fork <id> reads the source session but
                // writes changes to a brand-new session file.
                if (fork && !string.IsNullOrEmpty(sessionId))
                {
                    a.Add("--fork");
                    a.Add(sessionId);
                }
                // Only resume when we own the session id; `--continue` would
                // resume pi's global most-recent session → another conv.
                else if (continueSession && !string.IsNullOrEmpty(sessionId))
                {
                    a.Add("--session");
                    a.Add(sessionId);
                }
                if (!string.IsNullOrWhiteSpace(s.PiModel))
                {
                    a.Add("--model");
                    a.Add(s.PiModel);
                }
                if (!string.IsNullOrWhiteSpace(s.PiThinking))
                {
                    a.Add("--thinking");
                    a.Add(s.PiThinking);
                }
                // Skills: the UI owns which skills to load. If the user has
                // toggled any skills on, we disable Pi's default discovery
                // and pass `--skill <path>` explicitly so the set is exact.
                // When nothing is toggled we stay out of Pi's way and let
                // its normal discovery run (preserves backwards behaviour).
                if (skillPaths != null && skillPaths.Count > 0)
                {
                    a.Add("--no-skills");
                    foreach (var p in skillPaths)
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        a.Add("--skill");
                        a.Add(p);
                    }
                }
                return ("pi", a.ToArray());
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// Codex has no non-interactive `fork` subcommand, so we duplicate the
    /// parent's session file under a new UUID and resume the copy. Codex
    /// treats the filename's UUID as the session id, so the copy writes its
    /// own trail and the parent stays pristine. Returns the new session id
    /// on success, or null if the source file couldn't be located/copied.
    private static string? ForkCodexSessionFile(string sourceSessionId)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "sessions");
            if (!Directory.Exists(root)) return null;
            var match = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).IndexOf(sourceSessionId, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null) return null;
            var newUuid = Guid.NewGuid().ToString("D");
            var dir = Path.GetDirectoryName(match)!;
            var newName = Path.GetFileName(match)
                .Replace(sourceSessionId, newUuid, StringComparison.OrdinalIgnoreCase);
            var dest = Path.Combine(dir, newName);
            File.Copy(match, dest, overwrite: false);
            return newUuid;
        }
        catch
        {
            return null;
        }
    }
}
