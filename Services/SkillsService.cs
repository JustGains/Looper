using System.IO;

namespace JustCode.Services;

/// Manages Pi skills on disk. Skills are directories containing SKILL.md, and
/// Pi discovers them in `~/.pi/agent/skills/`, `~/.agents/skills/`, plus the
/// project-local `.pi/skills/` and `.agents/skills/`. The JustCode wrapper
/// enumerates all four locations, seeds a built-in "android-mcp" skill (the
/// former Android MCP pass-through, now a real skill), and exposes
/// create/delete so the skills menu can manage them.
public sealed record SkillEntry(string Name, string Path, string Origin, string Description);

public static class SkillsService
{
    public const string AndroidSkillName = "android-mcp";

    /// All known skill roots, in Pi's discovery order. Per-project roots are
    /// appended by `GetRoots(workingDirectory)`; the first four (user-level)
    /// are shared across projects.
    public static IReadOnlyList<(string Path, string Origin)> GetRoots(string? workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<(string, string)>
        {
            (System.IO.Path.Combine(home, ".pi", "agent", "skills"), "user (~/.pi)"),
            (System.IO.Path.Combine(home, ".agents", "skills"), "user (~/.agents)"),
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            list.Add((System.IO.Path.Combine(workingDirectory, ".pi", "skills"), "project (.pi)"));
            list.Add((System.IO.Path.Combine(workingDirectory, ".agents", "skills"), "project (.agents)"));
        }
        return list;
    }

    /// The canonical root we write new/user-added skills into.
    public static string WritableUserRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(home, ".pi", "agent", "skills");
    }

    // Cache discovery results per working-dir for a short window. Callers like
    // ConversationViewModel.GetEnabledSkillPaths and RefreshSkills re-enter
    // Discover on the hot path (every run start, every popup open, and used
    // to be called in every conv ctor). Walking 4 dirs + reading first lines
    // of N SKILL.md files isn't free when multiplied by N conversations.
    private const int CacheTtlMs = 1500;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<SkillEntry> Value)> _discoverCache
        = new(StringComparer.OrdinalIgnoreCase);
    private static bool _seedChecked;

    /// Enumerate every skill found under every root. If the same skill name
    /// appears in multiple roots the first occurrence (Pi's resolution order)
    /// wins and shadowed copies are dropped — mirrors how Pi actually loads.
    public static List<SkillEntry> Discover(string? workingDirectory)
    {
        // Seed the android-mcp skill exactly once per process — not on every
        // Discover call. It's idempotent on disk but the File.Exists check
        // was happening for every conversation ctor.
        if (!_seedChecked) { _seedChecked = true; EnsureAndroidSkillSeeded(); }

        var cacheKey = workingDirectory ?? "";
        if (_discoverCache.TryGetValue(cacheKey, out var entry)
            && (DateTime.UtcNow - entry.At).TotalMilliseconds < CacheTtlMs)
        {
            return entry.Value;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SkillEntry>();
        foreach (var (root, origin) in GetRoots(workingDirectory))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!seen.Add(name)) continue;
                    var skillFile = System.IO.Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;
                    result.Add(new SkillEntry(name, dir, origin, ReadDescription(skillFile)));
                }
            }
            catch { }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _discoverCache[cacheKey] = (DateTime.UtcNow, result);
        return result;
    }

    /// Drop the cached discovery so the next call rescans disk. Called after
    /// create/delete so the new state shows up immediately.
    public static void InvalidateCache() => _discoverCache.Clear();

    /// Create a new skill with the given name at the writable user root. The
    /// skill directory is created along with a SKILL.md stub the user can
    /// flesh out later. Returns the created path or null if the name clashes.
    public static string? CreateSkill(string name, string? description = null, string? body = null)
    {
        name = SanitizeName(name);
        if (string.IsNullOrEmpty(name)) return null;

        var root = WritableUserRoot();
        Directory.CreateDirectory(root);
        var dir = System.IO.Path.Combine(root, name);
        if (Directory.Exists(dir)) return null;
        Directory.CreateDirectory(dir);

        var desc = string.IsNullOrWhiteSpace(description)
            ? $"Use this skill when the user asks about {name}."
            : description.Trim();
        var md = body ?? BuildSkillTemplate(name, desc);
        File.WriteAllText(System.IO.Path.Combine(dir, "SKILL.md"), md);
        InvalidateCache();
        return dir;
    }

    /// Build a SKILL.md body that satisfies the Agent Skills spec frontmatter
    /// (name + description required). Used by CreateSkill and the seeded
    /// android-mcp repair path.
    private static string BuildSkillTemplate(string name, string description) =>
        $@"---
name: {name}
description: {EscapeYamlScalar(description)}
---

# {name}

{description}

## Steps
1. Describe what to do
2. Add more steps as needed
";

    private static string EscapeYamlScalar(string s)
    {
        // Conservative: quote if the value contains any YAML-significant
        // characters (colon, hash, leading dash, etc.) or leading/trailing
        // whitespace. Empty string → quoted empty.
        if (string.IsNullOrEmpty(s)) return "\"\"";
        bool needsQuotes = s != s.Trim()
            || s.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`' }) >= 0
            || s.StartsWith("-") || s.StartsWith("?");
        if (!needsQuotes) return s;
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// True if the file's first non-whitespace content is a `---` line followed
    /// by YAML containing a `description:` field, closed by another `---`.
    /// Matches pi's validation: the only skill-blocking violation is a missing
    /// description, so that's what we check.
    public static bool HasValidFrontmatter(string skillFile)
    {
        try
        {
            if (!File.Exists(skillFile)) return false;
            var lines = File.ReadLines(skillFile).Take(80).ToList();
            // Skip leading blank lines
            int i = 0;
            while (i < lines.Count && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Count || lines[i].Trim() != "---") return false;
            i++;
            bool sawDescription = false;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.Trim() == "---") return sawDescription;
                // Look for `description:` or `description:` at start of a line
                // (indentation is invalid at the top level of frontmatter).
                if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    var tail = line.Substring("description:".Length).Trim();
                    if (tail.Length > 0 && tail != "\"\"" && tail != "''") sawDescription = true;
                }
                i++;
            }
            return false;
        }
        catch { return false; }
    }

    /// Delete a skill by path. Guarded against paths that don't live under
    /// one of the known roots so we never nuke arbitrary directories.
    public static bool DeleteSkill(string path, string? workingDirectory)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            bool underKnownRoot = false;
            foreach (var (root, _) in GetRoots(workingDirectory))
            {
                var r = System.IO.Path.GetFullPath(root);
                if (full.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(System.IO.Path.GetDirectoryName(full), r, StringComparison.OrdinalIgnoreCase))
                {
                    underKnownRoot = true;
                    break;
                }
            }
            if (!underKnownRoot) return false;
            if (!Directory.Exists(full)) return false;
            Directory.Delete(full, recursive: true);
            InvalidateCache();
            return true;
        }
        catch { return false; }
    }

    /// Idempotently writes the android-mcp skill to the user-level root. Done
    /// once at startup (or on each Discover call) so the port replaces the
    /// old `PiAndroidMcp` toggle without requiring any manual setup from the
    /// user. If an existing file is missing the required frontmatter we
    /// rewrite it (earlier versions shipped without frontmatter, which pi
    /// rejects as "description is required").
    public static void EnsureAndroidSkillSeeded()
    {
        try
        {
            var dir = System.IO.Path.Combine(WritableUserRoot(), AndroidSkillName);
            var file = System.IO.Path.Combine(dir, "SKILL.md");
            Directory.CreateDirectory(dir);
            if (File.Exists(file) && HasValidFrontmatter(file)) return;
            File.WriteAllText(file, AndroidSkillContent);
        }
        catch { }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            else if (ch == ' ') sb.Append('-');
        }
        return sb.ToString().Trim('-', '_');
    }

    private static string ReadDescription(string skillFile)
    {
        try
        {
            // Prefer the frontmatter `description:` field (Agent Skills spec).
            // Fall back to the first non-header line for legacy / freeform files.
            var lines = File.ReadLines(skillFile).Take(80).ToList();
            int i = 0;
            while (i < lines.Count && lines[i].Trim().Length == 0) i++;
            if (i < lines.Count && lines[i].Trim() == "---")
            {
                i++;
                while (i < lines.Count && lines[i].Trim() != "---")
                {
                    var line = lines[i];
                    if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line.Substring("description:".Length).Trim().Trim('"').Trim('\'');
                        if (value.Length > 0)
                            return value.Length > 120 ? value.Substring(0, 117) + "…" : value;
                    }
                    i++;
                }
            }
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith('#') || t == "---") continue;
                return t.Length > 120 ? t.Substring(0, 117) + "…" : t;
            }
        }
        catch { }
        return "";
    }

    /// Ported from the CursorTouch/Android-MCP project. The original MCP
    /// exposes State/Click/Long-Click/Type/Swipe/Drag/Press/Wait/Notification/
    /// Shell tools; this skill spells out the adb equivalent the model can
    /// drive through Pi's built-in Bash tool. No MCP server required.
    private const string AndroidSkillContent = """
---
name: android-mcp
description: Drive a connected Android device or emulator through adb via the Bash tool — taps, swipes, typing, launching apps, reading UI state via screenshots or uiautomator, checking notifications, and running shell commands. Use this when the user asks to interact with Android.
---

# android-mcp

Use this skill when the user asks you to drive an Android device or emulator — taps, swipes, typing, launching apps, reading UI state, checking notifications, or running shell commands on the device. You have no Android MCP server; you drive everything through `adb` via the Bash tool.

## Prerequisites
- `adb` must be on PATH.
- A device must be connected (USB or `adb connect <host:port>` for Wi-Fi) and show up under `adb devices -l`.
- Android 10+ recommended. USB debugging must be enabled on the target.

## Discovery
Always confirm the target before acting:
```
adb devices -l
```
If there are multiple devices, target one explicitly with `-s <serial>` on every command below.

## Tool equivalents

### State — observe the device
The original State tool captures active app + interactive UI elements. The closest adb equivalents:
- Screenshot (fastest way to confirm UI state):
  ```
  adb exec-out screencap -p > /tmp/s.png
  ```
  Then read `/tmp/s.png` with the Read tool — it's a real image and the model can see it.
- UI hierarchy (XML dump of all interactive nodes with bounds):
  ```
  adb exec-out uiautomator dump /dev/tty
  ```
  Parse the XML to find `clickable="true"` / `text="…"` nodes and their `bounds="[x1,y1][x2,y2]"`.
- Active window / foreground package:
  ```
  adb shell dumpsys window | grep -E "mCurrentFocus|mFocusedApp"
  ```

### Click-Tool — single tap
```
adb shell input tap <x> <y>
```
Compute `<x> <y>` as the centre of the element's `bounds` from the uiautomator dump.

### Long-Click-Tool — extended press
```
adb shell input swipe <x> <y> <x> <y> 800
```
(A swipe with identical start/end and a duration >500ms is a long-press.)

### Type-Tool — text input
```
adb shell input text "<text>"
```
Spaces must be escaped as `%s`. For Unicode, prefer `adb shell input keyboard text` or paste via the clipboard helper if installed.

### Swipe-Tool — gesture between two points
```
adb shell input swipe <x1> <y1> <x2> <y2> <duration_ms>
```
Common durations: 200ms flick, 600ms scroll, 1000ms deliberate swipe.

### Drag-Tool — slow point-to-point drag
Same `input swipe` command, but pick a long duration (1200–2000ms) so the OS treats it as a drag rather than a flick.

### Press-Tool — hardware buttons
```
adb shell input keyevent <code>
```
Common keycodes: `3` HOME, `4` BACK, `24` VOL_UP, `25` VOL_DOWN, `26` POWER, `66` ENTER, `67` DEL, `82` MENU, `187` APP_SWITCH.

### Wait-Tool — pause
Use your shell's `sleep`:
```
sleep 1.5
```

### Notification-Tool — read notifications
```
adb shell dumpsys notification --noredact
```
Filter for the app of interest. For the notification shade UI itself, pull it down with a swipe from y=0 to ~y=800.

### Shell-Tool — arbitrary shell
Run anything on the device:
```
adb shell "<command>"
```
Useful one-liners:
- List installed packages: `adb shell pm list packages`
- Launch an activity: `adb shell am start -n <pkg>/<activity>`
- Stop an app: `adb shell am force-stop <pkg>`
- Recent logs: `adb logcat -d -t 200`
- Clear an app's data: `adb shell pm clear <pkg>`

## Working loop
1. `adb devices -l` to confirm the target.
2. Screenshot (`screencap -p`) and read the image to see the current UI.
3. If you need coordinates for a specific element, dump the UI hierarchy and read the `bounds` attribute.
4. Perform the action (tap / swipe / type / keyevent).
5. Screenshot again to verify the result.
6. Repeat.

Screenshots are cheap; always take one after an action you're unsure about rather than guessing whether it worked.
""";
}
