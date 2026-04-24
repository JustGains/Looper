using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TerminalHost(WebView2 webView)
    {
        _webView = webView;
    }

    public async Task InitializeAsync()
    {
        if (_webViewReady) return;

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
        Post(new { type = "destroy", id = s.Id });
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

        if (!started)
        {
            try { s.Start(_pendingCols, _pendingRows); }
            catch (Exception ex)
            {
                var msg = $"\r\n\x1b[31m[terminal] failed to start {s.Shell.Exe}: {ex.Message}\x1b[0m\r\n";
                var bytes = Encoding.UTF8.GetBytes(msg);
                var b64 = Convert.ToBase64String(bytes);
                Post(new { type = "stdout", id = s.Id, b64 });
            }
        }
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
                    // Page finished loading; replay any state queued during init.
                    if (_panel != null)
                    {
                        foreach (var s in _panel.Sessions) Post(new { type = "create", id = s.Id });
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
                            if (_bySessionId.TryGetValue(id, out var s))
                                s.Resize(cols, rows);
                        }
                    }
                    break;
            }
        }
        catch
        {
            // Malformed payload — ignore. The page should be well-behaved.
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
