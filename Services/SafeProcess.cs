using System.Diagnostics;

namespace JustCode.Services;

/// <summary>Zero-ceremony wrapper around Process.Start that swallows all
/// exceptions so the UI doesn't crash on missing executables or permission
/// issues. Used by view models that just want to "fire and forget".</summary>
public static class SafeProcess
{
    public static void Start(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments ?? "") { UseShellExecute = true });
        }
        catch { }
    }
}
