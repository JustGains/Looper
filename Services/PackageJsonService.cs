using System.IO;
using System.Text.Json;

namespace JustCode.Services;

public sealed record PackageScript(string Name, string Command);

/// One discovered package.json. Root is `IsRoot=true`; anything nested is a
/// workspace package keyed by its relative path to the working directory.
public sealed record PackageInfo(
    string DirPath,
    string DisplayName,
    bool IsRoot,
    string PackageManager,
    IReadOnlyList<PackageScript> Scripts);

/// A node in the hierarchical script menu. Scripts whose names contain `:` are
/// grouped under a parent node so e.g. `test:unit`, `test:e2e`, `test:watch`
/// become a `test` submenu with three leaves. A top-level `test` script
/// co-exists as a leaf on its own at the `test` level (menu items carry the
/// full script name in `FullName`).
public sealed class ScriptNode
{
    public string Segment { get; set; } = "";
    public string? FullName { get; set; } // non-null → leaf (runnable)
    public string? Command { get; set; }
    public List<ScriptNode> Children { get; } = new();
    public bool IsLeaf => FullName != null;
    public bool HasChildren => Children.Count > 0;
}

public static class PackageJsonService
{
    public const int MaxPackages = 10;
    /// Max directory depth below the project root to search. Depth 0 = root,
    /// 1 = direct children, 2 = grandchildren, 3 = great-grandchildren. Three
    /// covers typical monorepo layouts like `apps/web`, `packages/ui/`, and
    /// `services/api/foo/` without descending into deeply nested vendor code.
    public const int MaxDepth = 3;

    /// Walk the project tree (skipping heavy/hidden/ignored dirs) to find up
    /// to MaxPackages package.json files. The root project (if present) is
    /// always first and flagged IsRoot=true. Stops early once the limit is
    /// hit to avoid descending into monorepos forever.
    public static List<PackageInfo> Discover(string workingDirectory)
    {
        var results = new List<PackageInfo>();
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return results;

        // Root package.json first (if any)
        var rootPkg = Path.Combine(workingDirectory, "package.json");
        if (File.Exists(rootPkg))
        {
            var info = TryParse(workingDirectory, rootPkg, isRoot: true);
            if (info != null) results.Add(info);
        }

        // Respect .gitignore if present — a pragmatic subset: ignore node_modules,
        // common build dirs, and anything explicitly listed. We don't implement
        // full gitignore semantics (negations, globs) — just line-by-line dir
        // name matches plus a hardcoded skip list.
        var skipDirs = BuildSkipSet(workingDirectory);

        // DFS with depth tracking. Root is depth 0; direct children are 1.
        // Stop descending once depth exceeds MaxDepth.
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((workingDirectory, 0));
        while (stack.Count > 0 && results.Count < MaxPackages)
        {
            var (dir, depth) = stack.Pop();
            if (depth >= MaxDepth) continue;
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                if (results.Count >= MaxPackages) break;
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith('.')) continue; // .git, .vscode, .idea, etc.
                if (skipDirs.Contains(name)) continue;

                var pkg = Path.Combine(sub, "package.json");
                if (File.Exists(pkg) && !string.Equals(sub, workingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var info = TryParse(workingDirectory, pkg, isRoot: false);
                    if (info != null) results.Add(info);
                }
                stack.Push((sub, depth + 1));
            }
        }

        return results;
    }

    /// Build a ScriptNode tree from a flat script list. Scripts are split on
    /// `:` and each segment becomes a node. A script like "test" sits as a
    /// leaf, and "test:unit" slots underneath the "test" group.
    public static ScriptNode BuildTree(IReadOnlyList<PackageScript> scripts)
    {
        var root = new ScriptNode { Segment = "" };
        foreach (var s in scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var parts = s.Name.Split(':');
            var cursor = root;
            for (int i = 0; i < parts.Length; i++)
            {
                var seg = parts[i];
                var child = cursor.Children.FirstOrDefault(c => c.Segment == seg);
                if (child == null)
                {
                    child = new ScriptNode { Segment = seg };
                    cursor.Children.Add(child);
                }
                if (i == parts.Length - 1)
                {
                    // Leaf values (full script name + command) go on the
                    // terminal node. If a group with the same name already
                    // exists (e.g. "test" group and standalone "test" script),
                    // the standalone still runs — we attach it here and the
                    // menu renders both the group's submenu AND a direct leaf.
                    child.FullName = s.Name;
                    child.Command = s.Command;
                }
                cursor = child;
            }
        }
        return root;
    }

    public static string BuildRunCommand(string packageManager, string scriptName)
        => $"{packageManager} run {QuotePowerShell(scriptName)}";

    private static PackageInfo? TryParse(string workingDirectory, string packageJsonPath, bool isRoot)
    {
        try
        {
            var dir = Path.GetDirectoryName(packageJsonPath)!;
            var json = File.ReadAllText(packageJsonPath);
            using var doc = JsonDocument.Parse(json);

            string? nameField = null;
            if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                nameField = n.GetString();

            var scripts = new List<PackageScript>();
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in s.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var cmd = prop.Value.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(prop.Name)) continue;
                    scripts.Add(new PackageScript(prop.Name, cmd));
                }
            }

            // Prefer package.name → fallback to the relative path → leaf dir name.
            string display;
            if (isRoot)
            {
                display = !string.IsNullOrWhiteSpace(nameField)
                    ? nameField!
                    : Path.GetFileName(workingDirectory.TrimEnd('\\', '/'));
            }
            else
            {
                var rel = Path.GetRelativePath(workingDirectory, dir).Replace('\\', '/');
                display = !string.IsNullOrWhiteSpace(nameField)
                    ? $"{nameField}  ({rel})"
                    : rel;
            }

            return new PackageInfo(dir, display, isRoot, DetectPackageManager(dir), scripts);
        }
        catch { return null; }
    }

    /// Pick a package manager by looking for its lockfile next to package.json.
    /// Falls back to npm (universally present alongside node).
    private static string DetectPackageManager(string dir)
    {
        if (File.Exists(Path.Combine(dir, "bun.lockb")) || File.Exists(Path.Combine(dir, "bun.lock"))) return "bun";
        if (File.Exists(Path.Combine(dir, "pnpm-lock.yaml"))) return "pnpm";
        if (File.Exists(Path.Combine(dir, "yarn.lock"))) return "yarn";
        return "npm";
    }

    private static string QuotePowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";

    /// Hardcoded skip set plus any directory names listed in the project root
    /// .gitignore. Full gitignore semantics (negations, nested patterns, glob
    /// stars) are intentionally out of scope — this is a heuristic, not a
    /// compliance layer.
    private static HashSet<string> BuildSkipSet(string workingDirectory)
    {
        // Seed from the shared canonical list (same as the file explorer) so
        // a new build-output directory added to DirectorySkipList is picked up
        // by every walker without touching this method.
        var set = new HashSet<string>(DirectorySkipList.BuildOutputs, StringComparer.OrdinalIgnoreCase);
        try
        {
            var gitignore = Path.Combine(workingDirectory, ".gitignore");
            if (!File.Exists(gitignore)) return set;
            foreach (var raw in File.ReadLines(gitignore))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                // Strip leading /, trailing /, and glob chars — we only match
                // bare directory names. Anything fancier is ignored.
                line = line.TrimStart('/').TrimEnd('/');
                if (line.Length == 0) continue;
                if (line.Contains('*') || line.Contains('?') || line.Contains('/')) continue;
                set.Add(line);
            }
        }
        catch { }
        return set;
    }
}
