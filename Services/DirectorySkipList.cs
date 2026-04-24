namespace JustCode.Services;

/// <summary>Shared directory name filter used by every pane that walks the
/// project tree (file explorer, package.json discovery, etc.). Centralising
/// the blocklist avoids the two services drifting — if a new junk-output
/// directory gets added here, everybody picks it up.</summary>
public static class DirectorySkipList
{
    /// Canonical bare names of build/output/cache directories that are never
    /// interesting in a project view. Matches case-insensitively.
    public static readonly IReadOnlyCollection<string> BuildOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "dist", "build", "out", "target", ".next", ".nuxt",
        ".svelte-kit", ".turbo", ".cache", "coverage", ".output", ".venv",
        "venv", "__pycache__", "bin", "obj",
    };

    /// The explorer's stricter filter: everything in BuildOutputs, plus any
    /// dotfile directory (.git / .vscode / .idea / … — noisy and rarely
    /// what the user wants to browse).
    public static bool ShouldHideInTree(string? name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith('.')) return true;
        return BuildOutputs.Contains(name);
    }
}
