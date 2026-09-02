using System.IO;
using System.Text.Json;
using JustCode.Models;

namespace JustCode.Services;

public sealed class ConfigStore
{
    public const int MaxRecent = 10;
    public const string AppId = "com.justgains.looper";
    public const string FileName = "looper.conf";

    public string Path { get; }

    // Compact JSON: settings get written on every debounced UI change, and
    // indented output was roughly 2x larger + ~2x slower to serialize. The
    // file is machine-read only; no one scrubs it in an editor.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    public ConfigStore()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Path = System.IO.Path.Combine(appdata, AppId, FileName);
    }

    public LoopSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<LoopSettings>(File.ReadAllText(Path));
                if (cfg != null)
                {
                    if (cfg.StylingRules.Count == 0)
                        cfg.StylingRules = StylingDefaults.BuildDefaults();
                    else
                        MergeMissingDefaults(cfg.StylingRules);
                    foreach (var r in cfg.StylingRules) r.Compile();

                    MigrateLegacyProjectFields(cfg);
                    NormalizeRecentDirectories(cfg, keepMissing: true);
                    if (string.IsNullOrWhiteSpace(cfg.OpenRouterTitleModel)
                        || string.Equals(cfg.OpenRouterTitleModel, "openai/gpt-oss-safeguard-20b", StringComparison.OrdinalIgnoreCase))
                        cfg.OpenRouterTitleModel = LoopSettings.DefaultOpenRouterTitleModel;
                    return cfg;
                }
            }
        }
        catch { }

        var fresh = new LoopSettings { StylingRules = StylingDefaults.BuildDefaults() };
        foreach (var r in fresh.StylingRules) r.Compile();
        return fresh;
    }

    public void Save(LoopSettings s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            NormalizeRecentDirectories(s, keepMissing: true);
            File.WriteAllText(Path, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch { }
    }

    /// Push to RecentWorkingDirectories (folder-picker history only).
    public void PushRecent(LoopSettings s, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var normalized = Normalize(dir);
        RemoveRecent(s, normalized);
        s.RecentWorkingDirectories.Insert(0, normalized);
        NormalizeRecentDirectories(s, keepMissing: true);
    }

    public bool RemoveRecent(LoopSettings s, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var normalized = Normalize(dir);
        return s.RecentWorkingDirectories.RemoveAll(d =>
            string.Equals(Normalize(d), normalized, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public int RemoveMissingRecentDirectories(LoopSettings s)
    {
        NormalizeRecentDirectories(s, keepMissing: true);
        return s.RecentWorkingDirectories.RemoveAll(d =>
        {
            try { return !Directory.Exists(d); }
            catch { return true; }
        });
    }

    /// Append to OpenProjects without duplicates.
    public void AddOpenProject(LoopSettings s, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var normalized = Normalize(dir);
        if (s.OpenProjects.Any(d => string.Equals(Normalize(d), normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        s.OpenProjects.Add(normalized);
    }

    public void RemoveOpenProject(LoopSettings s, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var normalized = Normalize(dir);
        s.OpenProjects.RemoveAll(d =>
            string.Equals(Normalize(d), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string p)
    {
        try { return System.IO.Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p.TrimEnd('\\', '/'); }
    }

    private static void NormalizeRecentDirectories(LoopSettings s, bool keepMissing)
    {
        var unique = new List<string>(Math.Min(MaxRecent, s.RecentWorkingDirectories.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in s.RecentWorkingDirectories)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var normalized = Normalize(raw);
            if (!seen.Add(normalized)) continue;
            if (!keepMissing)
            {
                try
                {
                    if (!Directory.Exists(normalized)) continue;
                }
                catch { continue; }
            }
            unique.Add(normalized);
            if (unique.Count >= MaxRecent) break;
        }
        s.RecentWorkingDirectories = unique;
    }

    private static void MergeMissingDefaults(List<StylingRule> existing)
    {
        var defaults = StylingDefaults.BuildDefaults();
        var haveNames = new HashSet<string>(
            existing.Select(r => r.Name ?? ""),
            StringComparer.OrdinalIgnoreCase);
        int insertAt = 0;
        foreach (var d in defaults)
        {
            if (haveNames.Contains(d.Name)) continue;
            existing.Insert(insertAt++, d);
        }
    }

    private static void MigrateLegacyProjectFields(LoopSettings cfg)
    {
        // If OpenProjects is empty but we have legacy hints, seed them.
        if (cfg.OpenProjects.Count == 0)
        {
            var candidates = new[] { cfg.LastWorkingDirectory, cfg.WorkingDirectory }
                .Concat(cfg.RecentWorkingDirectories);
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                try { if (Directory.Exists(c)) { cfg.OpenProjects.Add(Normalize(c)); break; } }
                catch { }
            }
        }
        if (string.IsNullOrWhiteSpace(cfg.ActiveProject))
            cfg.ActiveProject = cfg.OpenProjects.FirstOrDefault();

        // Clear legacy fields so they don't re-serialize.
        cfg.LastWorkingDirectory = null;
        cfg.WorkingDirectory = null;
    }
}
