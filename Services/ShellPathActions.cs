using System.IO;

namespace JustCode.Services;

/// <summary>Shared shell and clipboard helpers for arbitrary file-system paths.
/// Keeps small UI actions consistent and failure-tolerant.</summary>
public static class ShellPathActions
{
    public static void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try { System.Windows.Clipboard.SetText(text); } catch { }
    }

    public static void CopyPath(string? path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return;

        CopyText(normalized);
    }

    public static void RevealInExplorer(string? path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return;

        if (File.Exists(normalized))
        {
            SafeProcess.Start("explorer.exe", $"/select,\"{normalized}\"");
            return;
        }

        if (Directory.Exists(normalized))
        {
            SafeProcess.Start("explorer.exe", $"\"{normalized}\"");
            return;
        }

        var parent = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            SafeProcess.Start("explorer.exe", $"\"{parent}\"");
    }

    public static void Open(string? path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return;

        if (Directory.Exists(normalized))
        {
            SafeProcess.Start("explorer.exe", $"\"{normalized}\"");
            return;
        }

        if (File.Exists(normalized))
        {
            SafeProcess.Start(normalized);
            return;
        }

        var parent = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            SafeProcess.Start("explorer.exe", $"\"{parent}\"");
    }

    public static void OpenTerminalHere(string? path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return;

        var dir = Directory.Exists(normalized)
            ? normalized
            : Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        // Prefer Windows Terminal; only fall back to cmd.exe if wt.exe isn't
        // installed. Previously we launched BOTH in parallel, which left
        // users with two terminal windows on every click.
        if (SafeProcess.TryStart("wt.exe", $"-d \"{dir}\"")) return;
        SafeProcess.Start("cmd.exe", $"/K cd /d \"{dir}\"");
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
}
