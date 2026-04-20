using System.Diagnostics;
using System.IO;
using System.Text;
using Looper.Models;

namespace Looper.Services;

public sealed class CliProcessRunner
{
    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;

    private Process? _process;
    private readonly object _lock = new();

    public async Task<int> RunAsync(LoopSettings settings, string prompt, bool continueSession, string? sessionId, CancellationToken ct)
    {
        var (fileName, args) = BuildCommand(settings, continueSession, sessionId);
        var workingDirectory = settings.WorkingDirectory;
        var resolved = ResolveExecutable(fileName);
        if (resolved == null)
        {
            ErrorLine?.Invoke(this, $"[looper] executable not found on PATH: {fileName}");
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

    private static (string fileName, string[] args) BuildCommand(LoopSettings s, bool continueSession, string? sessionId)
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
                };
                if (continueSession && !string.IsNullOrEmpty(sessionId))
                {
                    a.Add("--resume");
                    a.Add(sessionId);
                }
                else if (continueSession)
                {
                    a.Add("--continue");
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
                if (continueSession)
                {
                    // `codex exec resume` only accepts a subset of `codex exec` flags.
                    // --color / -m / -c are NOT valid here; the resumed session
                    // inherits its model/effort from creation time.
                    a.Add("resume");
                    a.Add("--dangerously-bypass-approvals-and-sandbox");
                    a.Add("--skip-git-repo-check");
                    if (!string.IsNullOrEmpty(sessionId))
                        a.Add(sessionId);
                    else
                        a.Add("--last");
                }
                else
                {
                    a.Add("--dangerously-bypass-approvals-and-sandbox");
                    a.Add("--skip-git-repo-check");
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
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
