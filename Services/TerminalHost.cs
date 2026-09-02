using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using JustCode.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JustCode.Services;

/// <summary>
/// Glues a <see cref="WebView2"/> control hosting xterm.js to a
/// <see cref="TerminalPanelViewModel"/>. The panel owns the ConPTY-backed
/// sessions; this class forwards their output into the WebView, lifecycle
/// events into creates/destroys/activates, and WebView-originated messages
/// (stdin/resize) back into the appropriate session.
/// </summary>
public sealed class TerminalHost : IDisposable
{
    private readonly WebView2 _webView;
    private readonly Dictionary<string, TerminalSessionViewModel> _bySessionId = new();
    private readonly Dictionary<string, EventHandler<ReadOnlyMemory<byte>>> _outputHandlers = new();
    private TerminalPanelViewModel? _panel;
    private bool _webViewReady;
    private bool _disposed;
    private int _pendingCols = 80;
    private int _pendingRows = 24;
    // Last font size the JS side told us about. Replayed on panel rebind so a
    // user's zoom choice persists across project switches without round-tripping
    // through disk-backed settings.
    private int? _lastFontSize;
    // Last banner width JS reported. Used to bound `TrimReason` so the empty-
    // state caption fits the current pane width instead of a hard-coded cap.
    // Pixels → approx characters at the banner's 11px font (~6px wide).
    private int _bannerWidthPx = 480;

    // Resize debouncing. xterm/FitAddon emits a `resize` per character on slow
    // drag-resizes, which hammers ConPTY with intermediate sizes (and on
    // Windows that's a syscall + COM thunk per pump). We coalesce per session:
    // remember the latest cols/rows and flush after a short idle window.
    private const int ResizeDebounceMs = 50;
    private readonly Dictionary<string, (int cols, int rows)> _pendingResize = new();
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _resizeTimers = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TerminalHost(WebView2 webView)
    {
        _webView = webView;
    }

    /// <summary>
    /// Optional shell-id provider consulted before the cmd / detected-shell
    /// walk during AddSession recovery. Lets a caller pin a user-configured
    /// fallback priority (e.g. always try pwsh before cmd) without reaching
    /// into <see cref="ShellDetector"/> directly.
    /// <para/>
    /// Threading: invoked from the WebView dispatcher (UI thread) at the
    /// moment a `new-session` recovery runs. The implementation must be
    /// safe to call there. Reading from <c>LoopSettings</c> is the canonical
    /// case and is UI-thread-safe; if a future caller wires the callback
    /// to an off-thread source, it owns synchronization.
    /// </summary>
    public Func<IEnumerable<string>>? FallbackShellOrder { get; set; }

    /// <summary>
    /// Re-pushes the WPF accent brush as a CSS custom property to the page.
    /// Public so a future theme-switch dialog can keep the embedded
    /// terminal in lockstep with app-wide theme changes — today only the
    /// `ready` handler invokes it.
    /// </summary>
    public void RefreshTheme() => PostTheme();

    public async Task InitializeAsync()
    {
        if (_webViewReady) return;

        // Best-effort housekeeping before we start a new session: the
        // clipboard temp dir only grows otherwise. Cheap (single dir
        // enumeration) and fully off the hot path.
        PruneClipboardTempDir();

        // Keep user data tied to the app so we don't pollute their profile.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JustCode", "WebView2");
        Directory.CreateDirectory(userDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        // Disable browser chrome that doesn't fit a terminal.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        // Serve the vendored terminal page from inside the build output.
        var appDir = Path.GetDirectoryName(typeof(TerminalHost).Assembly.Location)!;
        var assetDir = Path.Combine(appDir, "Assets", "terminal");
        core.SetVirtualHostNameToFolderMapping(
            "terminal.local", assetDir, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate("http://terminal.local/index.html");

        _webViewReady = true;
    }

    public void AttachPanel(TerminalPanelViewModel? panel)
    {
        if (ReferenceEquals(_panel, panel)) return;

        // Detach from the previous panel — tell the page to drop those sessions.
        if (_panel != null)
        {
            _panel.SessionAdded -= OnSessionAdded;
            _panel.SessionRemoved -= OnSessionRemoved;
            _panel.ActiveSessionChanged -= OnActiveSessionChanged;
            ((INotifyCollectionChanged)_panel.Sessions).CollectionChanged -= OnSessionsCollectionChanged;
            foreach (var s in _bySessionId.Values.ToArray())
                DetachSessionIO(s);
            if (_webViewReady)
                foreach (var id in _bySessionId.Keys.ToArray())
                    Post(new { type = "destroy", id });
            _bySessionId.Clear();
        }

        _panel = panel;
        if (_panel == null) return;

        _panel.SessionAdded += OnSessionAdded;
        _panel.SessionRemoved += OnSessionRemoved;
        _panel.ActiveSessionChanged += OnActiveSessionChanged;
        ((INotifyCollectionChanged)_panel.Sessions).CollectionChanged += OnSessionsCollectionChanged;

        // Seed the bridge with whatever sessions already exist.
        foreach (var s in _panel.Sessions) RegisterSession(s, started: true);
        if (_panel.ActiveSession != null)
            Post(new { type = "activate", id = _panel.ActiveSession.Id });
        // Replay the user's last font-size choice so a project switch doesn't
        // reset the zoom level the user set on the previous panel.
        if (_lastFontSize is int fs)
            Post(new { type = "set-font-size", value = fs });
        // Re-push theme as a side benefit: keeps the RefreshTheme path warm
        // (so it doesn't bit-rot) and picks up any Accent change made since
        // the original `ready` fired.
        if (_webViewReady) PostTheme();
    }

    public void FocusActive()
    {
        if (_panel?.ActiveSession != null)
            Post(new { type = "focus", id = _panel.ActiveSession.Id });
    }

    public void ClearActive()
    {
        if (_panel?.ActiveSession != null)
            Post(new { type = "clear", id = _panel.ActiveSession.Id });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _resizeTimers.Values)
        {
            try { t.Stop(); } catch { }
        }
        _resizeTimers.Clear();
        _pendingResize.Clear();
        try { AttachPanel(null); } catch { }
        try { _webView.CoreWebView2?.Stop(); } catch { }
    }

    // ---- panel events ----

    private void OnSessionAdded(object? sender, TerminalSessionViewModel s)
        => RegisterSession(s, started: false);

    private void OnSessionRemoved(object? sender, TerminalSessionViewModel s)
    {
        if (!_bySessionId.ContainsKey(s.Id)) return;
        DetachSessionIO(s);
        _bySessionId.Remove(s.Id);
        if (_resizeTimers.TryGetValue(s.Id, out var t))
        {
            try { t.Stop(); } catch { }
            _resizeTimers.Remove(s.Id);
        }
        _pendingResize.Remove(s.Id);
        Post(new { type = "destroy", id = s.Id });
    }

    private void QueueResize(string id, int cols, int rows)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_bySessionId.ContainsKey(id)) return;
        _pendingResize[id] = (cols, rows);
        if (!_resizeTimers.TryGetValue(id, out var timer))
        {
            timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(ResizeDebounceMs),
            };
            timer.Tick += (_, _) => FlushPendingResize(id);
            _resizeTimers[id] = timer;
        }
        // Restart the idle timer — each new resize event extends the window
        // until the user stops dragging.
        timer.Stop();
        timer.Start();
    }

    private void FlushPendingResize(string id)
    {
        if (_resizeTimers.TryGetValue(id, out var timer))
            timer.Stop();
        if (!_pendingResize.TryGetValue(id, out var size)) return;
        _pendingResize.Remove(id);
        if (_bySessionId.TryGetValue(id, out var s))
        {
            try { s.Resize(size.cols, size.rows); }
            catch { /* pty may have closed mid-resize */ }
        }
    }

    private void OnActiveSessionChanged(object? sender, TerminalSessionViewModel s)
    {
        Post(new { type = "activate", id = s.Id });
    }

    private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ObservableCollection already invokes SessionAdded/Removed before
        // raising CollectionChanged — we just need this subscription to keep
        // the INotifyCollectionChanged reference alive for cleanup symmetry.
    }

    private void RegisterSession(TerminalSessionViewModel s, bool started)
    {
        if (_bySessionId.ContainsKey(s.Id)) return;
        _bySessionId[s.Id] = s;

        EventHandler<ReadOnlyMemory<byte>> handler = (_, bytes) =>
        {
            if (!_webViewReady) return;
            var b64 = Convert.ToBase64String(bytes.Span);
            Post(new { type = "stdout", id = s.Id, b64 });
        };
        s.Output += handler;
        _outputHandlers[s.Id] = handler;

        Post(new { type = "create", id = s.Id });
        ReplaySessionHistory(s);

        if (!started)
        {
            try { s.Start(_pendingCols, _pendingRows); }
            catch (Exception ex)
            {
                var msg = $"\r\n\x1b[31m[terminal] failed to start {s.Shell.Exe}: {ex.Message}\x1b[0m\r\n";
                var bytes = Encoding.UTF8.GetBytes(msg);
                s.RecordOutputHistory(bytes);
                var b64 = Convert.ToBase64String(bytes);
                Post(new { type = "stdout", id = s.Id, b64 });
            }
        }
    }

    private void ReplaySessionHistory(TerminalSessionViewModel s)
    {
        var history = s.GetOutputHistorySnapshot();
        if (history.Length == 0) return;
        var b64 = Convert.ToBase64String(history);
        Post(new { type = "stdout", id = s.Id, b64 });
    }

    private void DetachSessionIO(TerminalSessionViewModel s)
    {
        if (_outputHandlers.TryGetValue(s.Id, out var h))
        {
            try { s.Output -= h; } catch { }
            _outputHandlers.Remove(s.Id);
        }
    }

    // ---- WebView2 → panel ----

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    // Page finished loading; push the theme first so any
                    // CSS custom-property consumers (search hairline, etc.)
                    // pick up the WPF accent on first paint.
                    PostTheme();
                    // Replay any state queued during init.
                    if (_panel != null)
                    {
                        foreach (var s in _panel.Sessions)
                        {
                            Post(new { type = "create", id = s.Id });
                            ReplaySessionHistory(s);
                        }
                        if (_panel.ActiveSession != null)
                            Post(new { type = "activate", id = _panel.ActiveSession.Id });
                    }
                    break;
                case "stdin":
                    {
                        var id = root.GetProperty("id").GetString() ?? "";
                        var data = root.GetProperty("data").GetString() ?? "";
                        if (_bySessionId.TryGetValue(id, out var s))
                            s.Write(Encoding.UTF8.GetBytes(data));
                    }
                    break;
                case "resize":
                    {
                        var cols = root.GetProperty("cols").GetInt32();
                        var rows = root.GetProperty("rows").GetInt32();
                        _pendingCols = cols;
                        _pendingRows = rows;
                        if (root.TryGetProperty("id", out var idProp))
                        {
                            var id = idProp.GetString() ?? "";
                            QueueResize(id, cols, rows);
                        }
                    }
                    break;
                case "new-session":
                    // Ctrl+Shift+T from JS. Spawn on the bound panel using the
                    // user's default shell. Marshal to UI thread because
                    // Sessions is an ObservableCollection. If the spawn
                    // throws (e.g. detected shell missing on disk), surface
                    // a flash on the empty state so the keybinding doesn't
                    // feel silently broken.
                    if (_panel != null)
                    {
                        Action spawn = () =>
                        {
                            // Walked-fallback recovery: try the user's preferred
                            // shell first; on failure, walk every detected shell
                            // (cmd.exe gets first-class priority because it ships
                            // with Windows). Only the final fall-through emits an
                            // error flash; intermediate recovery is `info`.
                            try { _panel?.AddSession(); return; }
                            catch (Exception ex1)
                            {
                                System.Diagnostics.Debug.WriteLine($"[terminal] AddSession default failed: {ex1.Message}");

                                var firstError = TrimReason(ex1.Message);
                                var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                bool TrySpawn(string id)
                                {
                                    if (!tried.Add(id)) return false;
                                    try
                                    {
                                        _panel?.AddSession(id);
                                        Post(new
                                        {
                                            type = "flash-empty",
                                            kind = "info",
                                            reason = $"default shell failed; opened {id} instead ({firstError})"
                                        });
                                        return true;
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[terminal] AddSession({id}) failed: {ex.Message}");
                                        return false;
                                    }
                                }

                                // Apply user-tuned fallback priority first, then `cmd`,
                                // then everything detected. HashSet dedupes so repeats
                                // are no-ops.
                                if (FallbackShellOrder != null)
                                    foreach (var id in FallbackShellOrder())
                                        if (!string.IsNullOrWhiteSpace(id) && TrySpawn(id)) return;
                                if (TrySpawn("cmd")) return;
                                foreach (var sh in ShellDetector.Available)
                                    if (TrySpawn(sh.Id)) return;

                                Post(new
                                {
                                    type = "flash-empty",
                                    kind = "error",
                                    reason = $"couldn't start any shell: {firstError}"
                                });
                            }
                        };
                        if (_webView.Dispatcher.CheckAccess()) spawn();
                        else _webView.Dispatcher.BeginInvoke(spawn);
                    }
                    else
                    {
                        // Bridge isn't bound to a project — light up the
                        // empty-state so the keybinding feels acknowledged.
                        Post(new { type = "flash-empty" });
                    }
                    break;
                case "paste-request":
                    {
                        // navigator.clipboard.readText() can't run on this
                        // non-secure-context page, so the JS asks us to read
                        // the clipboard and post the text back.
                        var id = root.GetProperty("id").GetString() ?? "";
                        string text;
                        try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
                        catch { text = ""; }
                        if (text.Length > 0)
                            Post(new { type = "paste", id, text, warnMultiline = true });
                        else
                            Post(new { type = "paste-empty", id, kind = "text" });
                    }
                    break;
                case "copy":
                    {
                        var text = root.GetProperty("text").GetString() ?? "";
                        if (text.Length > 0)
                        {
                            try { System.Windows.Clipboard.SetText(text); }
                            catch { /* clipboard can be locked by another process */ }
                        }
                    }
                    break;
                case "image-paste-request":
                    {
                        // Save the clipboard image to a temp PNG and paste its
                        // path. Lets users drop screenshots into agents like
                        // Claude Code that accept image references by path.
                        // Falls back to a text paste if no image is present.
                        var id = root.GetProperty("id").GetString() ?? "";
                        var path = TrySaveClipboardImage();
                        if (path != null)
                        {
                            // Quote the path if it contains spaces or
                            // characters most shells treat as special. The
                            // shell on the other end will get a path it can
                            // pass to a tool without re-escaping.
                            var quoted = QuoteShellPath(path, _bySessionId.TryGetValue(id, out var sess) ? sess.Shell : null);
                            Post(new { type = "paste", id, text = quoted });
                        }
                        else
                        {
                            string text;
                            try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
                            catch { text = ""; }
                            if (text.Length > 0)
                                Post(new { type = "paste", id, text, warnMultiline = true });
                            else
                                Post(new { type = "paste-empty", id, kind = "image" });
                        }
                    }
                    break;
                case "font-size":
                    // JS-side font size changed (Ctrl+= / -). Persist on the
                    // panel so a re-bind can replay the value.
                    if (root.TryGetProperty("value", out var fsProp) &&
                        fsProp.TryGetInt32(out var fs))
                    {
                        _lastFontSize = fs;
                    }
                    break;
            }
        }
        catch
        {
            // Malformed payload — ignore. The page should be well-behaved.
        }
    }

    /// <summary>
    /// Surround <paramref name="path"/> with shell-appropriate quoting if it
    /// contains characters most shells treat as separators or syntax. Returns
    /// the input unchanged when no quoting is necessary.
    /// </summary>
    private static string QuoteShellPath(string path, ShellProfile? shell)
    {
        if (string.IsNullOrEmpty(path)) return path;
        // Conservative trigger: any whitespace, parens, brackets, or shell
        // metachars. Plain ASCII paths under tempdir typically need none.
        if (path.IndexOfAny(new[] { ' ', '\t', '(', ')', '[', ']', '{', '}', '&', '|', ';', '<', '>', '`', '$', '"', '\'' }) < 0)
            return path;

        // PowerShell uses single quotes for literal strings (no expansion);
        // doubles its embedded apostrophes. cmd.exe and POSIX shells both
        // accept double quotes, with `\"` to embed a literal in POSIX. We
        // detect PowerShell by exe name; everything else falls through to
        // double-quote escaping which works for cmd, bash, zsh, fish, sh.
        var exe = (shell?.Exe ?? "").ToLowerInvariant();
        var isPwsh = exe.EndsWith("powershell.exe") || exe.EndsWith("pwsh.exe")
            || exe.EndsWith("powershell") || exe.EndsWith("pwsh");
        if (isPwsh)
            return "'" + path.Replace("'", "''") + "'";
        // cmd.exe and Windows-side bash/CLI agents accept literal Windows
        // paths inside double quotes without backslash escaping; only the
        // embedded `"` needs work.
        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Drop clipboard PNGs older than 30 days, and keep at most 200 files.
    /// Runs at host init; mtime is sufficient (we don't need atime). Silent
    /// on every failure — pruning is best-effort hygiene, not correctness.
    /// </summary>
    private static void PruneClipboardTempDir()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "JustCode", "clipboard");
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
            const int MaxFiles = 200;

            var files = new DirectoryInfo(dir).GetFiles("clip-*.png");
            // First pass: hard-expire by age.
            var survivors = new List<FileInfo>(files.Length);
            foreach (var f in files)
            {
                if (f.LastWriteTimeUtc < cutoff)
                {
                    try { f.Delete(); } catch { }
                }
                else
                {
                    survivors.Add(f);
                }
            }
            // Second pass: cap the total so a busy week can't fill /tmp. Keep
            // newest entries — likely re-pasted by an active agent.
            if (survivors.Count > MaxFiles)
            {
                survivors.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = MaxFiles; i < survivors.Count; i++)
                {
                    try { survivors[i].Delete(); } catch { }
                }
            }
        }
        catch
        {
            // No-op on any IO/permission error.
        }
    }

    /// <summary>
    /// Trims an exception message to the first non-empty line and clamps it
    /// to a length the empty-state banner can render without wrapping. The
    /// full message is still available via Debug.WriteLine for diagnostics.
    /// </summary>
    private static string TrimReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        const int MaxLen = 120;
        // First non-empty line — multi-line errors collapse to a useful
        // headline; the rest stays in the debug log.
        var line = message.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? message;
        line = line.Replace("\r", "").Trim();
        return line.Length <= MaxLen ? line : line.Substring(0, MaxLen - 1) + "…";
    }

    /// <summary>
    /// Reads the WPF Accent brush from app resources and posts it to the
    /// page as a CSS custom property. Single source of truth: when Accent
    /// changes in MainWindow.xaml, the embedded terminal picks it up
    /// without a parallel CSS edit. Falls back silently if the resource
    /// can't be resolved (e.g. pre-Application initialization).
    /// </summary>
    private void PostTheme()
    {
        try
        {
            var accent = "#0e639c";
            if (System.Windows.Application.Current?.TryFindResource("Accent")
                is System.Windows.Media.SolidColorBrush brush)
            {
                var c = brush.Color;
                accent = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
            }
            Post(new { type = "theme", accent });
        }
        catch
        {
            // Theme push is decorative — don't propagate failures.
        }
    }

    private static string? TrySaveClipboardImage()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return null;
            var img = System.Windows.Clipboard.GetImage();
            if (img == null) return null;

            var dir = Path.Combine(Path.GetTempPath(), "JustCode", "clipboard");
            Directory.CreateDirectory(dir);

            // Encode once into memory so we can hash the PNG bytes for
            // dedupe before deciding whether to write. Repeated pastes of
            // the same screenshot would otherwise pile up in temp until
            // %TEMP% janitor reaped them, and agents that key off path
            // identity (e.g. cache hits) benefit from a stable filename.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var bytes = ms.ToArray();

            var hashHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            // Short suffix is plenty for collision-avoidance; full hash bloats
            // the path and the user only sees this in shell echo / agent input.
            var hashShort = hashHex.AsSpan(0, 16).ToString();

            // Reuse an existing dedupe-tagged file if its hash matches; the
            // sort-friendly UTC prefix only applies to fresh writes.
            foreach (var existing in Directory.EnumerateFiles(dir, $"clip-*-{hashShort}.png"))
            {
                try
                {
                    // Verify length matches in case the file was truncated mid-write
                    // by a prior crash; if so, fall through and rewrite.
                    if (new FileInfo(existing).Length == bytes.LongLength)
                        return existing;
                }
                catch { }
            }

            // UTC + ISO-style timestamp so filenames sort lexically across
            // timezones and DST boundaries; basic-format (no colons) keeps
            // the path safe on Windows.
            var path = Path.Combine(
                dir,
                $"clip-{DateTime.UtcNow:yyyyMMdd'T'HHmmssfff}Z-{hashShort}.png");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private void Post(object payload)
    {
        if (!_webViewReady) return;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        try
        {
            if (_webView.Dispatcher.CheckAccess())
                _webView.CoreWebView2?.PostWebMessageAsJson(json);
            else
                _webView.Dispatcher.BeginInvoke(() => _webView.CoreWebView2?.PostWebMessageAsJson(json));
        }
        catch { }
    }
}
