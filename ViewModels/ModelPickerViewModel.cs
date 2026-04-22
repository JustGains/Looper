using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using JustCode.Models;

namespace JustCode.ViewModels;

/// Shared searchable model picker used by every CLI tool (Claude, Codex, Pi).
/// The current tool is set by the consumer before opening the popup; the
/// picker seeds itself from that tool's model list and favourites, and
/// (for Pi) can live-refresh from `pi --list-models`.
public sealed class ModelPickerViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly LoopSettings _settings;
    private readonly Action _persist;
    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _all = new();

    public ObservableCollection<ModelEntry> Visible { get; } = new();

    private CliTool _tool;
    public CliTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            ReloadForTool();
            OnChanged();
            OnChanged(nameof(CanRefreshFromCli));
            OnChanged(nameof(ToolLabel));
        }
    }

    public bool CanRefreshFromCli => _tool == CliTool.Pi;
    public string ToolLabel => _tool switch
    {
        CliTool.ClaudeCode => "Claude",
        CliTool.Codex => "Codex",
        CliTool.Pi => "Pi",
        _ => "",
    };

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (_search != value) { _search = value; Rebuild(); OnChanged(); } }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; OnChanged(); } }
    }

    public int TotalCount => _all.Count;

    public ModelPickerViewModel(LoopSettings settings, Action persist, CliTool initialTool)
    {
        _settings = settings;
        _persist = persist;
        _tool = initialTool;
        ReloadForTool();
    }

    private void ReloadForTool()
    {
        _favorites.Clear();
        foreach (var f in GetFavoritesList()) _favorites.Add(f);
        _search = "";
        SeedModels();
        Rebuild();
        OnChanged(nameof(Search));
        OnChanged(nameof(TotalCount));
    }

    private List<string> GetFavoritesList() => _tool switch
    {
        CliTool.ClaudeCode => _settings.ClaudeFavoriteModels,
        CliTool.Codex => _settings.CodexFavoriteModels,
        CliTool.Pi => _settings.PiFavoriteModels,
        _ => new List<string>(),
    };

    private void WriteFavoritesList(List<string> v)
    {
        switch (_tool)
        {
            case CliTool.ClaudeCode: _settings.ClaudeFavoriteModels = v; break;
            case CliTool.Codex: _settings.CodexFavoriteModels = v; break;
            case CliTool.Pi: _settings.PiFavoriteModels = v; break;
        }
    }

    private IReadOnlyList<string> SeedForTool() => _tool switch
    {
        CliTool.ClaudeCode => MainViewModel.ClaudeModels,
        CliTool.Codex => MainViewModel.CodexModels,
        CliTool.Pi => MainViewModel.PiModelsSeed,
        _ => Array.Empty<string>(),
    };

    private void SeedModels()
    {
        var merged = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        // For Pi, once `pi --list-models` has populated the cache, treat it
        // as authoritative — the seed list is hardcoded across providers the
        // user may not have access to (e.g. GitHub Copilot users would see
        // openai/* and anthropic/* entries they can't call). Fall back to
        // the seed only while the cache is empty.
        bool usePiCache = _tool == CliTool.Pi
                          && _settings.PiModelCache != null
                          && _settings.PiModelCache.Count > 0;
        if (usePiCache)
        {
            foreach (var m in _settings.PiModelCache!)
                if (!string.IsNullOrWhiteSpace(m)) merged.Add(m);
        }
        else
        {
            foreach (var m in SeedForTool())
                if (!string.IsNullOrWhiteSpace(m)) merged.Add(m);
        }
        foreach (var f in _favorites) merged.Add(f);
        _all.Clear();
        _all.AddRange(merged);
    }

    private void Rebuild()
    {
        Visible.Clear();
        var q = _search?.Trim() ?? "";
        IEnumerable<string> source = _all;
        if (q.Length > 0)
            source = _all.Where(m => m.Contains(q, StringComparison.OrdinalIgnoreCase));
        var ordered = source
            .OrderByDescending(m => _favorites.Contains(m))
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase);
        foreach (var m in ordered)
            Visible.Add(new ModelEntry(m, _favorites.Contains(m), this));
    }

    public void ToggleFavorite(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        if (!_favorites.Add(model)) _favorites.Remove(model);
        WriteFavoritesList(_favorites.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList());
        _persist();
        Rebuild();
    }

    /// Kick off `pi --list-models` in the background (Pi only). No-op for
    /// Claude/Codex — they have fixed, known model lists.
    /// When `clearFirst` is true (user-triggered "Refresh" button) we empty
    /// the cache + visible list before spawning pi, so the user sees the
    /// list flash empty → repopulate and knows the refresh actually ran.
    public async Task RefreshFromCliAsync(bool clearFirst = false)
    {
        if (_tool != CliTool.Pi || IsLoading) return;
        if (clearFirst)
        {
            _settings.PiModelCache = new List<string>();
            _persist();
            _all.Clear();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Visible.Clear();
                OnChanged(nameof(TotalCount));
            });
        }
        IsLoading = true;
        try
        {
            var discovered = await Task.Run(InvokePiListModels);
            if (discovered.Count > 0)
            {
                _settings.PiModelCache = discovered
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _persist();
                SeedModels();
                Application.Current?.Dispatcher.Invoke(Rebuild);
            }
            else if (clearFirst)
            {
                // Refresh returned nothing — fall back to seed so the picker
                // isn't permanently empty. User still sees something usable.
                SeedModels();
                Application.Current?.Dispatcher.Invoke(Rebuild);
            }
        }
        catch { /* swallow — picker falls back to seed list */ }
        finally { IsLoading = false; OnChanged(nameof(TotalCount)); }
    }

    private static List<string> InvokePiListModels()
    {
        var models = new List<string>();
        try
        {
            // On Windows, npm installs pi as `pi.cmd` — CreateProcess does NOT
            // auto-resolve .cmd/.bat extensions, so `new ProcessStartInfo("pi")`
            // silently fails to launch. Resolve the real file first, then shell
            // it through cmd.exe if it's a batch file. (Mirrors the logic in
            // CliProcessRunner so both paths behave the same.)
            var resolved = ResolvePiExecutable();
            ProcessStartInfo psi;
            if (resolved == null)
            {
                // Best-effort fallback: let the OS search. Works on non-Windows
                // where `pi` is a real executable.
                psi = new ProcessStartInfo("pi")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("--list-models");
            }
            else
            {
                var ext = System.IO.Path.GetExtension(resolved).ToLowerInvariant();
                if (ext == ".cmd" || ext == ".bat")
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    psi.ArgumentList.Add("/d");
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add(resolved);
                    psi.ArgumentList.Add("--list-models");
                }
                else
                {
                    psi = new ProcessStartInfo(resolved)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    psi.ArgumentList.Add("--list-models");
                }
            }
            using var p = Process.Start(psi);
            if (p == null) return models;
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(10_000)) { try { p.Kill(entireProcessTree: true); } catch { } return models; }
            // WaitForExit(int) doesn't flush pending async stdout events — the
            // parameterless overload does. Skipping this drops trailing lines
            // (we were losing ~6 of 25 models from `pi --list-models`).
            p.WaitForExit();
            if (p.ExitCode != 0) return models;

            // Pi emits a column-aligned table:
            //   provider        model                   context  max-out  thinking  images
            //   github-copilot  claude-opus-4.7         144K     64K      yes       yes
            //   anthropic/*     claude-sonnet-4-6       ...
            // We want `<provider>/<model>` (what --model accepts). Accept any
            // row whose first token != "provider" (header) and split on runs
            // of two-or-more spaces so provider names containing a single
            // space/hyphen still survive. Also tolerate the legacy
            // one-token-per-line format for older Pi builds.
            foreach (var raw in sb.ToString().Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                var t = line.TrimStart();
                if (t.StartsWith('-') || t.StartsWith('=')) continue; // rules
                if (t.StartsWith("provider", StringComparison.OrdinalIgnoreCase) &&
                    t.Contains("model", StringComparison.OrdinalIgnoreCase)) continue; // header

                // Legacy format: already "provider/model" with no whitespace.
                if (!t.Contains(' ') && t.Contains('/'))
                {
                    models.Add(t);
                    continue;
                }

                // Column format: split on >=2 spaces so multi-word columns
                // don't break, then take provider + model.
                var cols = System.Text.RegularExpressions.Regex.Split(t, @"\s{2,}");
                if (cols.Length < 2) continue;
                var provider = cols[0].Trim();
                var model = cols[1].Trim();
                if (provider.Length == 0 || model.Length == 0) continue;
                if (provider.Contains('/')) { models.Add(provider); continue; }
                models.Add(provider + "/" + model);
            }
        }
        catch { }
        return models;
    }

    /// Resolve `pi` on PATH the same way CliProcessRunner does — accept any
    /// PATHEXT extension, so `pi.cmd` (how npm installs it on Windows) is
    /// found. Returns the full path or null.
    private static string? ResolvePiExecutable()
    {
        try
        {
            var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in pathext)
                {
                    var candidate = System.IO.Path.Combine(dir.Trim(), "pi" + ext);
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
                var bare = System.IO.Path.Combine(dir.Trim(), "pi");
                if (System.IO.File.Exists(bare) && System.IO.Path.GetExtension(bare).Length > 0) return bare;
            }
        }
        catch { }
        return null;
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ModelEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ModelPickerViewModel _owner;

    public string Name { get; }
    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));
        }
    }
    public string FavoriteGlyph => _isFavorite ? "★" : "☆";

    public ModelEntry(string name, bool isFavorite, ModelPickerViewModel owner)
    {
        Name = name;
        _isFavorite = isFavorite;
        _owner = owner;
    }

    public void ToggleFavorite() { _owner.ToggleFavorite(Name); }
}
