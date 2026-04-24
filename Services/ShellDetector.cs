using System.IO;

namespace JustCode.Services;

public sealed record ShellProfile(
    string Id,
    string Label,
    string Exe,
    string Args);

/// <summary>
/// Detects which shells are available on this machine. Results are cached
/// for the lifetime of the app — installing pwsh while JustCode is running
/// requires an app restart to pick it up.
/// </summary>
public static class ShellDetector
{
    private static IReadOnlyList<ShellProfile>? _cached;
    private static readonly object _lock = new();

    public static IReadOnlyList<ShellProfile> Available
    {
        get
        {
            if (_cached != null) return _cached;
            lock (_lock)
            {
                if (_cached != null) return _cached;
                _cached = Detect();
                return _cached;
            }
        }
    }

    /// <summary>
    /// Resolves a shell id (possibly empty or stale) to an actual profile.
    /// Falls back to the first detected shell — pwsh > powershell > cmd.
    /// </summary>
    public static ShellProfile Resolve(string? preferredId)
    {
        var list = Available;
        if (list.Count == 0)
        {
            // Absolute last resort. cmd.exe ships with Windows.
            return new ShellProfile("cmd", "Command Prompt", "cmd.exe", "");
        }
        if (!string.IsNullOrEmpty(preferredId))
        {
            foreach (var p in list)
                if (string.Equals(p.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                    return p;
        }
        return list[0];
    }

    private static IReadOnlyList<ShellProfile> Detect()
    {
        var found = new List<ShellProfile>();

        // Prefer PowerShell 7+ (pwsh). Check PATH + common install location.
        var pwshFromPath = FindOnPath("pwsh.exe");
        if (pwshFromPath != null)
        {
            found.Add(new ShellProfile("pwsh", "PowerShell", pwshFromPath, "-NoLogo"));
        }
        else
        {
            var pwshDefault = @"C:\Program Files\PowerShell\7\pwsh.exe";
            if (File.Exists(pwshDefault))
                found.Add(new ShellProfile("pwsh", "PowerShell", pwshDefault, "-NoLogo"));
        }

        // Windows PowerShell (always present on Windows 10+).
        var winPs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                  "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs))
            found.Add(new ShellProfile("powershell", "Windows PowerShell", winPs, "-NoLogo"));

        // Classic cmd.
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (File.Exists(cmd))
            found.Add(new ShellProfile("cmd", "Command Prompt", cmd, ""));

        // Git Bash.
        foreach (var root in new[] {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe" })
        {
            if (File.Exists(root))
            {
                found.Add(new ShellProfile("git-bash", "Git Bash", root, "--login -i"));
                break;
            }
        }

        // WSL default distro.
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (File.Exists(wsl))
            found.Add(new ShellProfile("wsl", "WSL", wsl, ""));

        return found;
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
