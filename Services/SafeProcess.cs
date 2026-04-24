using System.Diagnostics;

namespace JustCode.Services;

/// <summary>Zero-ceremony wrapper around Process.Start that swallows all
/// exceptions so the UI doesn't crash on missing executables or permission
/// issues. Used by view models that just want to "fire and forget".</summary>
public static class SafeProcess
{
    public static void Start(string fileName, string? arguments = null) =>
        TryStart(fileName, arguments);

    /// <summary>Same fire-and-forget semantics as Start, but reports whether
    /// the process was actually spawned. Callers that want a graceful fallback
    /// (e.g. try wt.exe, fall back to cmd.exe if Windows Terminal isn't
    /// installed) use this so they don't end up launching both at once.</summary>
    public static bool TryStart(string fileName, string? arguments = null)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(fileName, arguments ?? "") { UseShellExecute = true });
            return p != null;
        }
        catch { return false; }
    }
}
