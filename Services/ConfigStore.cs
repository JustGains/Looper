using System.IO;
using System.Text.Json;
using Looper.Models;

namespace Looper.Services;

public sealed class ConfigStore
{
    public const int MaxRecent = 10;
    public const string AppId = "com.justgains.looper";
    public const string FileName = "looper.conf";

    public string Path { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
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
                    return cfg;
                }
            }
        }
        catch { }

        var fresh = new LoopSettings { StylingRules = StylingDefaults.BuildDefaults() };
        foreach (var r in fresh.StylingRules) r.Compile();
        return fresh;
    }

    /// Inject any default rule whose Name isn't already in the list,
    /// preserving user-customised rules and their order. New defaults are
    /// prepended so they take priority on tie-matches.
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

    public void Save(LoopSettings s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch { }
    }

    public void PushRecent(LoopSettings s, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var normalized = System.IO.Path.GetFullPath(dir).TrimEnd('\\', '/');
        s.RecentWorkingDirectories.RemoveAll(d =>
            string.Equals(SafeNormalize(d), normalized, StringComparison.OrdinalIgnoreCase));
        s.RecentWorkingDirectories.Insert(0, normalized);
        while (s.RecentWorkingDirectories.Count > MaxRecent)
            s.RecentWorkingDirectories.RemoveAt(s.RecentWorkingDirectories.Count - 1);
        s.LastWorkingDirectory = normalized;
    }

    private static string SafeNormalize(string p)
    {
        try { return System.IO.Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }
}
