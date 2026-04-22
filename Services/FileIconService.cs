using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace JustCode.Services;

/// Resolves a filename (or directory name) to a rendered WPF icon using the
/// material-icon-theme SVG set. The manifest was extracted from the upstream
/// TypeScript source at build-integration time (see
/// `vendor/material-icon-theme/extract-manifest.mjs`) into `Assets/icon-map.json`.
public static class FileIconService
{
    // null entries are cached too, so we don't re-probe disk for every node
    // whose extension has no material icon.
    private static readonly ConcurrentDictionary<string, DrawingImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IconMap> _map = new(LoadMap);
    private static readonly WpfDrawingSettings _svgSettings = new()
    {
        IncludeRuntime = false,
        TextAsGeometry = true,
    };

    public static DrawingImage? GetFileIcon(string fileName)
    {
        var map = _map.Value;
        var name = (fileName ?? "").ToLowerInvariant();
        if (name.Length == 0) return LoadByName(map.Default);

        // 1. Exact filename match (e.g. "package.json", "dockerfile", ".gitignore")
        if (map.Filenames.TryGetValue(name, out var byName))
        {
            var img = LoadByName(byName);
            if (img != null) return img;
        }

        // 2. Multi-part extension (".schema.json", ".config.ts"). Material
        //    sometimes keys by composite extensions, so try longest first.
        var parts = name.Split('.');
        for (int start = 1; start < parts.Length; start++)
        {
            var ext = string.Join('.', parts.Skip(start));
            if (map.Extensions.TryGetValue(ext, out var byExt))
            {
                var img = LoadByName(byExt);
                if (img != null) return img;
            }
        }

        // 3. Fallback default
        return LoadByName(map.Default);
    }

    public static DrawingImage? GetFolderIcon(string folderName, bool open = false)
    {
        var map = _map.Value;
        var name = (folderName ?? "").ToLowerInvariant();
        var table = open ? map.FolderNamesOpen : map.FolderNames;
        if (name.Length > 0 && table.TryGetValue(name, out var iconName))
        {
            var img = LoadByName(iconName);
            if (img != null) return img;
        }
        return LoadByName(open ? map.FolderDefaultOpen : map.FolderDefault);
    }

    /// Returns null when no SVG exists for this icon name so the caller
    /// can fall through to the default. Previously we returned a sentinel
    /// empty image, which looked like a successful lookup and short-circuited
    /// the fallback — breaking default folder/file icons for unknown entries.
    private static DrawingImage? LoadByName(string iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return null;
        return _cache.GetOrAdd(iconName, LoadSvgSafe);
    }

    private static DrawingImage? LoadSvgSafe(string iconName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", iconName + ".svg");
            if (!File.Exists(path)) return null;
            var reader = new FileSvgReader(_svgSettings);
            var drawing = reader.Read(new Uri(path));
            if (drawing == null) return null;
            var img = new DrawingImage(drawing);
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static IconMap LoadMap()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icon-map.json");
            if (!File.Exists(path)) return new IconMap();
            var json = File.ReadAllText(path);
            var map = JsonSerializer.Deserialize<IconMap>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }) ?? new IconMap();
            // Defensive lower-casing in case the manifest didn't already normalize.
            map.Extensions = map.Extensions.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
            map.Filenames = map.Filenames.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
            map.FolderNames = map.FolderNames.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
            map.FolderNamesOpen = map.FolderNamesOpen.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
            return map;
        }
        catch { return new IconMap(); }
    }

    private sealed class IconMap
    {
        public string Default { get; set; } = "file";
        public string FolderDefault { get; set; } = "folder";
        public string FolderDefaultOpen { get; set; } = "folder-open";
        public Dictionary<string, string> Extensions { get; set; } = new();
        public Dictionary<string, string> Filenames { get; set; } = new();
        public Dictionary<string, string> FolderNames { get; set; } = new();
        public Dictionary<string, string> FolderNamesOpen { get; set; } = new();
    }
}
