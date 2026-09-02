using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using JustCode.Services;
using JustCode.ViewModels;
using Microsoft.Win32;

namespace JustCode;

public partial class MainWindow : Window
{
    /// Window-scoped RoutedCommand for Ctrl+Shift+K. Wired in the Window
    /// constructor to invoke ClearQueue on the active conversation.
    public static readonly RoutedCommand ClearQueueCommand = new("ClearQueue", typeof(MainWindow));

    private readonly MainViewModel _vm;
    private ProjectViewModel? _subscribedProject;
    private ConversationViewModel? _subscribedConversation;
    private System.Windows.Threading.DispatcherTimer? _wordWrapTimer;
    private TerminalHost? _terminalHost;
    private bool _terminalHostReady;
    private ProjectViewModel? _terminalAttachedProject;

    // Yolo full-window terminal: a second WebView2 + TerminalHost dedicated
    // to the per-conversation yolo CLI session. Re-attached to the active
    // conversation's YoloPanel whenever SelectedConversation changes.
    private TerminalHost? _yoloTerminalHost;
    private bool _yoloTerminalHostReady;
    private ConversationViewModel? _yoloAttachedConversation;

    // @-mention state
    private readonly Dictionary<string, FileMentionIndex> _mentionIndexes = new(StringComparer.OrdinalIgnoreCase);
    private int _mentionTokenStart = -1; // position of '@' in the PromptBox when popup is active
    private static readonly Regex MentionRef = new(
        @"@(?:""([^""]+)""|([^\s""]+))", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        CommandBindings.Add(new CommandBinding(
            ClearQueueCommand,
            (_, _) => _vm.SelectedProject?.SelectedConversation?.ClearQueue(),
            (_, e) =>
            {
                e.CanExecute = _vm.SelectedProject?.SelectedConversation?.HasQueuedMessages == true;
                e.Handled = true;
            }));
        RestoreWindowBounds();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ProjectAdded += (_, p) => HookProject(p);
        _vm.ProjectRemoved += (_, p) => UnhookProject(p);

        // SizeChanged fires on every animation frame during a resize. Throttle
        // to a DispatcherTimer so we only recompute PageWidth once per burst
        // — otherwise FlowDocument re-layouts thrash the UI thread.
        HookConsoleBoxSizeChanged(ConsoleAllBox);
        HookConsoleBoxSizeChanged(ConsoleConversationBox);
        HookConsoleBoxSizeChanged(ConsoleToolsBox);

        Loaded += (_, _) =>
        {
            // Kick icon warmup off the UI thread before any tree renders —
            // SharpVectors class init is otherwise paid on the first render.
            FileIconService.Prewarm();
            _vm.InitializeTabs(Directory.GetCurrentDirectory());
            foreach (var p in _vm.Projects) HookProject(p);
            SubscribeToSelectedProject();
            SubscribeToSelectedConversation();
            AttachSelectedConversationDocuments();
            ApplyWordWrap();
            AttachMentionHighlightAdorner();
            UpdateActivityBarStyles();
            _ = InitializeTerminalHostAsync();
            _ = InitializeYoloTerminalHostAsync();
        };

        Closing += (_, _) => _vm.SaveWindowBounds(Left, Top, Width, Height);
        Closed += (_, _) =>
        {
            try { _terminalHost?.Dispose(); } catch { }
            try { _yoloTerminalHost?.Dispose(); } catch { }
            _vm.Shutdown();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableImmersiveDarkMode();
        TryPaintInitialDarkBackground();
    }

    // ---- hooking projects and their conversations ----

    private void HookProject(ProjectViewModel p)
    {
        p.ConversationAdded -= OnConversationAdded;
        p.ConversationAdded += OnConversationAdded;
        p.ConversationRemoved -= OnConversationRemoved;
        p.ConversationRemoved += OnConversationRemoved;
        foreach (var c in p.Conversations) HookConversation(c);
    }

    private void UnhookProject(ProjectViewModel p)
    {
        p.ConversationAdded -= OnConversationAdded;
        p.ConversationRemoved -= OnConversationRemoved;
        foreach (var c in p.Conversations) UnhookConversation(c);
    }

    private void OnConversationAdded(object? sender, ConversationViewModel c) => HookConversation(c);
    private void OnConversationRemoved(object? sender, ConversationViewModel c) => UnhookConversation(c);

    private void HookConversation(ConversationViewModel c)
    {
        c.ConsoleAppend -= OnConversationConsoleAppend;
        c.ConsoleAppend += OnConversationConsoleAppend;
        // Replay persisted console history once, on first hook. Goes straight
        // through AppendStyled rather than the buffered streaming path so the
        // FlowDocument is populated synchronously — the buffer's 16ms
        // DispatcherTimer can sit unfired for seconds during startup while
        // the UI thread is busy, which made non-selected tabs look empty
        // until clicked.
        var history = c.PopConsoleHistory();
        if (!string.IsNullOrEmpty(history))
            AppendStyled(c, history);
    }

    private void UnhookConversation(ConversationViewModel c)
    {
        c.ConsoleAppend -= OnConversationConsoleAppend;
        if (_consoleFlushTimers.TryGetValue(c, out var timer))
        {
            timer.Stop();
            _consoleFlushTimers.Remove(c);
        }
        _consoleBuffers.Remove(c);
    }

    /// Per-conversation buffer of pending chunks. Rapidly-streaming models
    /// (Claude, Codex, pi) can emit hundreds of deltas per second. Appending
    /// each to the FlowDocument separately forces a re-layout per delta;
    /// coalescing into a single ~60 Hz flush cuts render time dramatically.
    private readonly Dictionary<ConversationViewModel, System.Text.StringBuilder> _consoleBuffers = new();
    private readonly Dictionary<ConversationViewModel, System.Windows.Threading.DispatcherTimer> _consoleFlushTimers = new();
    private const int ConsoleFlushIntervalMs = 16;

    private void OnConversationConsoleAppend(object? sender, string chunk)
    {
        if (sender is not ConversationViewModel c || string.IsNullOrEmpty(chunk)) return;
        if (!_consoleBuffers.TryGetValue(c, out var sb))
        {
            sb = new System.Text.StringBuilder();
            _consoleBuffers[c] = sb;
        }
        sb.Append(chunk);
        if (!_consoleFlushTimers.TryGetValue(c, out var timer))
        {
            timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ConsoleFlushIntervalMs),
            };
            timer.Tick += (_, _) => FlushConsoleBuffer(c);
            _consoleFlushTimers[c] = timer;
        }
        if (!timer.IsEnabled) timer.Start();
    }

    private void FlushConsoleBuffer(ConversationViewModel c)
    {
        if (_consoleFlushTimers.TryGetValue(c, out var timer)) timer.Stop();
        if (!_consoleBuffers.TryGetValue(c, out var sb) || sb.Length == 0) return;
        var pending = sb.ToString();
        sb.Clear();
        AppendStyled(c, pending);
        if (ReferenceEquals(c, _vm.SelectedProject?.SelectedConversation)
            && _vm.AutoScrollConsole)
        {
            GetActiveConsoleBox()?.ScrollToEnd();
        }
    }

    // ---- selection changes ----

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedProject))
        {
            CloseInlineGitDiff();
            SubscribeToSelectedProject();
            SubscribeToSelectedConversation();
            AttachSelectedConversationDocuments();
            ApplyWordWrap();
            QueueScrollTasks();
            UpdateActivityBarStyles();
        }
        else if (e.PropertyName == nameof(MainViewModel.WordWrapConsole))
        {
            ApplyWordWrap();
        }
        else if (e.PropertyName == nameof(MainViewModel.AutoScrollTasks))
        {
            QueueScrollTasks();
        }
    }

    private void SubscribeToSelectedProject()
    {
        if (_subscribedProject != null)
            _subscribedProject.PropertyChanged -= OnProjectPropertyChanged;
        _subscribedProject = _vm.SelectedProject;
        if (_subscribedProject != null)
            _subscribedProject.PropertyChanged += OnProjectPropertyChanged;
        AttachActiveProjectTerminal();
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectViewModel.SelectedConversation))
        {
            CloseInlineGitDiff();
            SubscribeToSelectedConversation();
            AttachSelectedConversationDocuments();
            ApplyWordWrap();
            QueueScrollTasks();
            CloseMentionPopup();
            HideMentionTooltip();
        }
    }

    private void SubscribeToSelectedConversation()
    {
        if (_subscribedConversation != null)
            _subscribedConversation.PropertyChanged -= OnSelectedConversationPropertyChanged;
        _subscribedConversation = _vm.SelectedProject?.SelectedConversation;
        if (_subscribedConversation != null)
            _subscribedConversation.PropertyChanged += OnSelectedConversationPropertyChanged;
        AttachYoloConversationTerminal();
    }

    private void OnSelectedConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _subscribedConversation)) return;
        if (e.PropertyName is nameof(ConversationViewModel.TasksText)
            or nameof(ConversationViewModel.TaskPreviewMarkdown))
        {
            QueueScrollTasks();
        }
        else if (e.PropertyName == nameof(ConversationViewModel.IsTaskManagerEnabled))
        {
            // Toggling into yolo mode — make sure the attached panel actually
            // has a session running. EnsureYoloSession is idempotent.
            if (_subscribedConversation?.IsYoloModeEnabled == true)
                _subscribedConversation.EnsureYoloSession();
            AttachYoloConversationTerminal();
            _yoloTerminalHost?.FocusActive();
        }
    }

    private void AttachSelectedConversationDocuments()
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null)
        {
            ConsoleAllBox.Document = new FlowDocument();
            ConsoleConversationBox.Document = new FlowDocument();
            ConsoleToolsBox.Document = new FlowDocument();
            return;
        }
        if (!ReferenceEquals(ConsoleAllBox.Document, c.ConsoleDocument))
            ConsoleAllBox.Document = c.ConsoleDocument;
        if (!ReferenceEquals(ConsoleConversationBox.Document, c.ConversationConsoleDocument))
            ConsoleConversationBox.Document = c.ConversationConsoleDocument;
        if (!ReferenceEquals(ConsoleToolsBox.Document, c.ToolConsoleDocument))
            ConsoleToolsBox.Document = c.ToolConsoleDocument;
    }

    private void QueueScrollTasks()
    {
        Dispatcher.BeginInvoke(new Action(MaybeScrollTasks),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MaybeScrollTasks()
    {
        if (!_vm.AutoScrollTasks) return;
        if (TasksTabs == null) return;
        if (TasksTabs.SelectedIndex == 0)
        {
            TasksBox?.ScrollToEnd();
            return;
        }
        var fdsv = FindVisualChild<FlowDocumentScrollViewer>(TasksMarkdown);
        if (fdsv != null)
        {
            fdsv.ApplyTemplate();
            var sv = FindVisualChild<ScrollViewer>(fdsv);
            if (sv != null) { sv.ScrollToEnd(); return; }
        }
        var direct = FindVisualChild<ScrollViewer>(TasksMarkdown);
        direct?.ScrollToEnd();
    }

    private void ApplyWordWrap()
    {
        ApplyWordWrap(ConsoleAllBox);
        ApplyWordWrap(ConsoleConversationBox);
        ApplyWordWrap(ConsoleToolsBox);
    }

    private void HookConsoleBoxSizeChanged(RichTextBox box)
    {
        box.SizeChanged += (_, _) =>
        {
            if (_wordWrapTimer == null)
            {
                _wordWrapTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _wordWrapTimer.Tick += (_, _) => { _wordWrapTimer!.Stop(); ApplyWordWrap(); };
            }
            _wordWrapTimer.Stop();
            _wordWrapTimer.Start();
        };
    }

    private void ApplyWordWrap(RichTextBox box)
    {
        if (box?.Document == null) return;
        if (_vm.WordWrapConsole)
        {
            var w = Math.Max(100, box.ViewportWidth - 8);
            box.Document.PageWidth = w;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            box.Document.PageWidth = 6000;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    private RichTextBox? GetActiveConsoleBox()
        => ConsoleTabs?.SelectedIndex switch
        {
            1 => ConsoleConversationBox,
            2 => ConsoleToolsBox,
            _ => ConsoleAllBox,
        };

    private void ConsoleTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not TabControl) return;
        ApplyWordWrap();
        if (_vm.AutoScrollConsole)
            GetActiveConsoleBox()?.ScrollToEnd();
        if (ConsoleTabs.SelectedIndex == 3)
        {
            // First time the user opens the Terminal tab in this project,
            // auto-spawn a session so they land in a usable shell instead of
            // the empty-state screen.
            var panel = ActiveTerminalPanel;
            if (panel != null && !panel.HasAnySessions && _terminalHostReady)
                panel.AddSession();
            _terminalHost?.FocusActive();
        }
    }

    // ---- terminal panel (xterm.js + ConPTY) ----

    private async Task InitializeTerminalHostAsync()
    {
        if (_terminalHost != null) return;
        _terminalHost = new TerminalHost(TerminalWebView);
        // User-tunable fallback shell priority for AddSession recovery; the
        // host iterates this list before walking the detected default order.
        _terminalHost.FallbackShellOrder = () =>
            (_vm.Settings.TerminalShellFallbackOrder ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            await _terminalHost.InitializeAsync();
            _terminalHostReady = true;
            AttachActiveProjectTerminal();
        }
        catch
        {
            // WebView2 runtime not installed — fail quiet; user will see an
            // empty Terminal tab. We could surface a message here in a follow-up.
        }
    }

    private void AttachActiveProjectTerminal()
    {
        if (!_terminalHostReady || _terminalHost == null) return;
        var project = _vm.SelectedProject;
        if (ReferenceEquals(project, _terminalAttachedProject)) return;

        // Drop the previous panel's header-status hooks before swapping.
        if (_terminalAttachedProject?.TerminalPanel is { } previous)
        {
            ((System.Collections.Specialized.INotifyCollectionChanged)previous.Sessions)
                .CollectionChanged -= OnHeaderPanelSessionsChanged;
            previous.ActiveSessionChanged -= OnHeaderPanelActiveSessionChanged;
        }

        _terminalAttachedProject = project;
        _terminalHost.AttachPanel(project?.TerminalPanel);

        if (project?.TerminalPanel is { } next)
        {
            ((System.Collections.Specialized.INotifyCollectionChanged)next.Sessions)
                .CollectionChanged += OnHeaderPanelSessionsChanged;
            next.ActiveSessionChanged += OnHeaderPanelActiveSessionChanged;
        }
        UpdateTerminalHeaderStatus();
    }

    // The session whose Title we are currently mirroring into the header
    // status — kept here so we can detach the PropertyChanged hook when the
    // active session changes or the panel is swapped.
    private TerminalSessionViewModel? _headerTitleSession;

    private void OnHeaderPanelSessionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateTerminalHeaderStatus();

    private void OnHeaderPanelActiveSessionChanged(object? sender, TerminalSessionViewModel s)
        => UpdateTerminalHeaderStatus();

    private void OnHeaderTitleSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.Title))
            UpdateTerminalHeaderStatus();
    }

    private void UpdateTerminalHeaderStatus()
    {
        if (TerminalHeaderStatus == null) return;
        var panel = ActiveTerminalPanel;

        // Re-bind the title-tracking hook to whichever session is now active.
        var active = panel?.ActiveSession;
        if (!ReferenceEquals(_headerTitleSession, active))
        {
            if (_headerTitleSession != null)
                _headerTitleSession.PropertyChanged -= OnHeaderTitleSessionPropertyChanged;
            _headerTitleSession = active;
            if (_headerTitleSession != null)
                _headerTitleSession.PropertyChanged += OnHeaderTitleSessionPropertyChanged;
        }

        if (panel == null || panel.Sessions.Count == 0)
        {
            TerminalHeaderStatus.Text = "";
            return;
        }
        var count = panel.Sessions.Count;
        var title = active?.Title;
        var shell = active?.Shell.Label ?? "—";
        // "3 sessions · my-build (pwsh)" — show the user's title (or auto-
        // generated default) plus the underlying shell so two `pwsh` tabs are
        // distinguishable. Falls back to bare shell label if the title equals
        // the auto-generated `<Shell> (N)` form to avoid `pwsh (1) (pwsh)`.
        string label;
        if (string.IsNullOrEmpty(title) || title == shell)
            label = shell;
        else if (title.Contains($"({shell.ToLowerInvariant()})", StringComparison.OrdinalIgnoreCase)
              || title.StartsWith(shell + " ", StringComparison.OrdinalIgnoreCase))
            label = title; // already contains the shell
        else
            label = $"{title} ({shell})";

        TerminalHeaderStatus.Text = count == 1
            ? $"1 session · {label}"
            : $"{count} sessions · {label}";
    }

    // ---- yolo terminal panel (dedicated WebView2 for Task Manager-off mode) ----

    private async Task InitializeYoloTerminalHostAsync()
    {
        if (_yoloTerminalHost != null) return;
        _yoloTerminalHost = new TerminalHost(YoloTerminalWebView);
        try
        {
            await _yoloTerminalHost.InitializeAsync();
            _yoloTerminalHostReady = true;
            AttachYoloConversationTerminal();
        }
        catch
        {
            // WebView2 runtime not installed — yolo mode will fail soft.
        }
    }

    private void AttachYoloConversationTerminal()
    {
        if (!_yoloTerminalHostReady || _yoloTerminalHost == null) return;
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (ReferenceEquals(conv, _yoloAttachedConversation)) return;
        _yoloAttachedConversation = conv;
        _yoloTerminalHost.AttachPanel(conv?.YoloPanel);
        // First-time entry into yolo mode for this conversation kicks off the
        // CLI session so the user lands in a live REPL instead of an empty
        // pane. Idempotent — if a session is already running, this is a no-op.
        if (conv?.IsYoloModeEnabled == true) conv.EnsureYoloSession();
    }

    private void YoloRestart_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        conv.CloseYoloSession();
        conv.EnsureYoloSession();
        _yoloTerminalHost?.FocusActive();
    }

    private TerminalPanelViewModel? ActiveTerminalPanel =>
        _vm.SelectedProject?.TerminalPanel;

    private void TerminalAddSession_Click(object sender, RoutedEventArgs e)
    {
        ActiveTerminalPanel?.AddSession();
        _terminalHost?.FocusActive();
    }

    private void TerminalPickShell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var panel = ActiveTerminalPanel;
        if (panel == null) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var shells = ShellDetector.Available;
        if (shells.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No shells detected", IsEnabled = false });
        }
        else
        {
            var defaultId = _vm.DefaultShellId;
            foreach (var shell in shells)
            {
                var item = new MenuItem { Header = shell.Label, Tag = shell.Id };
                item.Click += (_, _) => panel.AddSession(shell.Id);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var header = new MenuItem
            {
                Header = "Default shell",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            };
            menu.Items.Add(header);
            foreach (var shell in shells)
            {
                var item = new MenuItem
                {
                    Header = shell.Label,
                    IsCheckable = true,
                    IsChecked = string.Equals(defaultId, shell.Id, StringComparison.OrdinalIgnoreCase)
                                || (string.IsNullOrEmpty(defaultId) && ReferenceEquals(shell, shells[0])),
                    StaysOpenOnClick = true,
                };
                item.Click += (_, _) => _vm.DefaultShellId = shell.Id;
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var editFallback = new MenuItem { Header = "Edit fallback order…" };
            editFallback.Click += (_, _) => OpenFallbackOrderEditor();
            menu.Items.Add(editFallback);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// Opens a small modal that lets the user edit
    /// <c>TerminalShellFallbackOrder</c> as a comma-separated list. Lists
    /// detected shells underneath the input as a hint. Saves on OK.
    /// </summary>
    private void OpenFallbackOrderEditor()
    {
        var dlg = new Window
        {
            Title = "Fallback shell order",
            Owner = this,
            Width = 500,
            Height = 220,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("BgSurface"),
            Foreground = (System.Windows.Media.Brush)FindResource("FgPrimary"),
            ShowInTaskbar = false,
        };
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(new TextBlock
        {
            Text = "Comma-separated shell ids tried in order when the preferred shell fails to spawn.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var input = new TextBox
        {
            Text = _vm.TerminalShellFallbackOrder,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(6, 4, 6, 4),
        };
        stack.Children.Add(input);
        var detected = string.Join(", ", ShellDetector.Available.Select(s => s.Id));
        stack.Children.Add(new TextBlock
        {
            Text = $"Detected: {(string.IsNullOrEmpty(detected) ? "(none)" : detected)}",
            Foreground = (System.Windows.Media.Brush)FindResource("FgDim"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12),
        });
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = "Save", MinWidth = 72, IsDefault = true };
        ok.Click += (_, _) =>
        {
            _vm.TerminalShellFallbackOrder = input.Text ?? "";
            dlg.DialogResult = true;
        };
        btnRow.Children.Add(cancel);
        btnRow.Children.Add(ok);
        stack.Children.Add(btnRow);
        dlg.Content = stack;
        dlg.Loaded += (_, _) => input.Focus();
        dlg.ShowDialog();
    }

    private void TerminalClear_Click(object sender, RoutedEventArgs e)
        => _terminalHost?.ClearActive();

    private void TerminalSaveOutput_Click(object sender, RoutedEventArgs e)
    {
        var panel = ActiveTerminalPanel;
        var session = panel?.ActiveSession;
        if (session == null) return;
        var snapshot = session.GetOutputHistorySnapshot();
        if (snapshot.Length == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Terminal output is empty.",
                "Save terminal output",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        // Replace path-hostile chars in the title so users with `<git-status>`
        // or similar in their tab titles don't get blocked at SaveFileDialog.
        var safeTitle = string.Concat((session.Title ?? "session")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        // Default suggested filename respects `TerminalSaveOutputLocalTime`:
        // local users ("when did I run this?") get `yyyyMMdd'T'HHmmss`, the
        // sortable-across-machines default sticks with UTC `…Z`.
        var stamp = _vm.Settings.TerminalSaveOutputLocalTime
            ? DateTime.Now.ToString("yyyyMMdd'T'HHmmss")
            : DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"terminal-{safeTitle}-{stamp}.log",
            // Filter index drives stripping: .log keeps the raw ANSI stream
            // (highest fidelity); .txt strips escapes for grep-friendly output.
            Filter = "Raw log with ANSI (*.log)|*.log|Plain text, ANSI stripped (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".log",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // FilterIndex is 1-based; 2 = "Plain text, ANSI stripped".
            byte[] bytes;
            if (dlg.FilterIndex == 2)
            {
                bytes = TerminalSessionViewModel.StripAnsi(snapshot, out var overflow);
                if (overflow > 0)
                {
                    // First-time signal that a session has ever produced a
                    // line longer than the 64 KiB line-buffer cap. Useful
                    // signal during debugging that a no-newline runaway
                    // stream actually exists in the wild.
                    System.Diagnostics.Debug.WriteLine(
                        $"[terminal] StripAnsi dropped {overflow} byte(s) for session '{session.Title}' (line-buffer cap)");
                }
            }
            else
            {
                bytes = snapshot;
            }
            File.WriteAllBytes(dlg.FileName, bytes);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Failed to save terminal output:\n{ex.Message}",
                "Save terminal output",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void TerminalCloseSession_Click(object sender, RoutedEventArgs e)
    {
        var panel = ActiveTerminalPanel;
        if (panel?.ActiveSession != null) panel.CloseSession(panel.ActiveSession);
    }

    private void TerminalCloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TerminalSessionViewModel s })
            ActiveTerminalPanel?.CloseSession(s);
        e.Handled = true;
    }

    // Drag-drop tab reorder state. Captured on left-mouse-down, consumed by
    // PreviewMouseMove once the user drags past the system threshold.
    private TerminalSessionViewModel? _terminalTabDragSource;
    private System.Windows.Point _terminalTabDragOrigin;

    private void TerminalTab_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TerminalSessionViewModel s) return;

        // Double-click on the tab opens the inline rename editor — matches
        // Windows Terminal / VS Code muscle memory. Right-click still works
        // as a discoverable secondary affordance.
        if (e.ClickCount == 2)
        {
            s.IsRenaming = true;
            e.Handled = true;
            return;
        }

        // Record drag origin so PreviewMouseMove can decide whether the user
        // intended a reorder vs. a click-to-activate. Cleared on drag start
        // or on the next mouse-down.
        _terminalTabDragSource = s;
        _terminalTabDragOrigin = e.GetPosition(this);

        var panel = ActiveTerminalPanel;
        if (panel != null) panel.ActiveSession = s;
        _terminalHost?.FocusActive();
    }

    private void TerminalTab_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (_terminalTabDragSource == null) return;
        if (sender is not FrameworkElement fe) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _terminalTabDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _terminalTabDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = _terminalTabDragSource;
        _terminalTabDragSource = null;
        try
        {
            System.Windows.DragDrop.DoDragDrop(
                fe,
                new System.Windows.DataObject("TerminalTabSession", source),
                System.Windows.DragDropEffects.Move);
        }
        catch { /* drag may fail mid-modal-popup; not worth surfacing */ }
    }

    private void TerminalTab_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var hasPayload = e.Data.GetDataPresent("TerminalTabSession");
        e.Effects = hasPayload
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;

        // Light up the indicator on the hovered tab; choose leading vs.
        // trailing edge based on whether the cursor is past the tab's center.
        // Clear any other tab's flag so only one indicator shows at a time.
        if (hasPayload && sender is FrameworkElement fe && fe.Tag is TerminalSessionViewModel target)
        {
            var source = e.Data.GetData("TerminalTabSession") as TerminalSessionViewModel;
            var pos = e.GetPosition(fe);
            var dropAfter = fe.ActualWidth > 0 && pos.X > fe.ActualWidth / 2.0;
            var panel = ActiveTerminalPanel;
            if (panel != null)
            {
                foreach (var s in panel.Sessions)
                {
                    var isThis = !ReferenceEquals(s, source) && ReferenceEquals(s, target);
                    s.IsDropTarget = isThis;
                    if (isThis) s.DropAfter = dropAfter;
                }
            }
        }
        e.Handled = true;
    }

    private void TerminalTab_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TerminalSessionViewModel s)
        {
            s.IsDropTarget = false;
            s.DropAfter = false;
        }
    }

    private void TerminalTab_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // Capture indicator state before clearing — the leading/trailing
        // decision was last computed in DragOver and lives on the target VM.
        var panel = ActiveTerminalPanel;
        var dropAfter = false;
        if (sender is FrameworkElement feProbe && feProbe.Tag is TerminalSessionViewModel probeTarget)
            dropAfter = probeTarget.DropAfter;
        if (panel != null)
            foreach (var s in panel.Sessions) { s.IsDropTarget = false; s.DropAfter = false; }

        if (sender is not FrameworkElement fe || fe.Tag is not TerminalSessionViewModel target) return;
        if (e.Data.GetData("TerminalTabSession") is not TerminalSessionViewModel source) return;
        if (ReferenceEquals(source, target)) { e.Handled = true; return; }
        if (panel == null) return;
        var src = panel.Sessions.IndexOf(source);
        var dst = panel.Sessions.IndexOf(target);
        if (src < 0 || dst < 0) return;

        // Trailing-edge drops mean "land after this tab". Adjust the index
        // and account for the source slot's removal shifting later indices
        // back by one when src < dst.
        if (dropAfter) dst++;
        if (src < dst) dst--;
        if (src == dst) { e.Handled = true; return; }
        panel.Sessions.Move(src, dst);
        e.Handled = true;
    }

    private void TerminalTab_MiddleClick_Close(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Chromium-tab convention: middle-click closes. The left-button
        // handler runs separately, so we filter to MiddleButton only and
        // mark Handled to keep the activation path from firing.
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        if (sender is FrameworkElement fe && fe.Tag is TerminalSessionViewModel s)
            ActiveTerminalPanel?.CloseSession(s);
        e.Handled = true;
    }

    private void TerminalTab_Rename_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TerminalSessionViewModel s)
        {
            s.IsRenaming = true;
            e.Handled = true;
        }
    }

    private void TerminalTab_TitleEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Escape)
        {
            if (tb.Tag is TerminalSessionViewModel s) s.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void TerminalTab_TitleEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is TerminalSessionViewModel s)
            s.IsRenaming = false;
    }

    private void TerminalTab_TitleEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // When the rename TextBox becomes visible (IsRenaming flipped on),
        // grab focus and select-all so typing immediately replaces the title.
        if (sender is not TextBox tb) return;
        if (e.NewValue is not bool visible || !visible) return;
        // Defer to after layout so Focus actually lands — the TextBox is
        // newly realized and won't take focus until it's measured.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            tb.Focus();
            tb.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RestoreWindowBounds()
    {
        var s = _vm.Settings;
        if (s.WindowWidth is > 200 && s.WindowHeight is > 200)
        {
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
        }
        if (s.WindowLeft is not null && s.WindowTop is not null)
        {
            Left = s.WindowLeft.Value;
            Top = s.WindowTop.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private static bool IsPlainText(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\'' || c == ',' || c == '.' || c == ';') continue;
            if (_stylingTriggerChars.Contains(c)) return false;
            // Anything non-ASCII (emoji/arrows/box drawing) is treated as a
            // potential match — styling rules DO use 🧠 ⎿ ▸ etc. for block
            // markers, so we can't short-circuit when those appear.
            if (c > 127) return false;
        }
        return true;
    }

    private void AppendStyled(ConversationViewModel c, string chunk)
    {
        int allLines = 0, conversationLines = 0, toolLines = 0;
        foreach (var line in SplitConsoleLines(ApplyCollapse(chunk)))
        {
            var cls = ConsoleLineClassifier.Classify(line);
            AppendStyledToParagraph(c.ConsoleParagraph, line);
            if (cls.IsCounted) allLines++;
            if (cls.IsTool)
            {
                AppendStyledToParagraph(c.ToolConsoleParagraph, line);
                if (cls.IsCounted) toolLines++;
            }
            else
            {
                var decision = c.RouteConversationLine(line);
                if (decision == ConversationViewModel.ConversationLineDecision.AppendBlankThenLine)
                    AppendStyledToParagraph(c.ConversationConsoleParagraph, "\n");
                if (decision != ConversationViewModel.ConversationLineDecision.Skip)
                {
                    AppendStyledToParagraph(c.ConversationConsoleParagraph, line);
                    if (cls.IsCounted) conversationLines++;
                }
            }
        }
        c.RecordConsoleLineCounts(allLines, conversationLines, toolLines);
    }

    private static IEnumerable<string> SplitConsoleLines(string chunk)
    {
        int i = 0;
        while (i < chunk.Length)
        {
            var nl = chunk.IndexOf('\n', i);
            if (nl < 0)
            {
                yield return chunk.Substring(i);
                yield break;
            }

            yield return chunk.Substring(i, nl - i + 1);
            i = nl + 1;
        }
    }

    private void AppendStyledToParagraph(Paragraph paragraph, string chunk)
    {
        var inlines = paragraph.Inlines;
        foreach (var (text, rule) in Tokenize(chunk))
        {
            var run = new Run(text);
            if (rule?.ForegroundBrush is not null) run.Foreground = rule.ForegroundBrush;
            if (rule?.BackgroundBrush is not null) run.Background = rule.BackgroundBrush;
            if (rule?.WeightValue is { } w) run.FontWeight = w;
            if (rule?.StyleValue is { } fs) run.FontStyle = fs;
            if (rule?.Underline == true) run.TextDecorations = TextDecorations.Underline;
            inlines.Add(run);
        }
        FlowDocumentInlineLimiter.Apply(inlines);
    }

    private string ApplyCollapse(string chunk)
    {
        if (!_vm.CollapseToolCalls || string.IsNullOrEmpty(chunk)) return chunk;

        var sb = new System.Text.StringBuilder(chunk.Length);
        int i = 0;
        while (i < chunk.Length)
        {
            var nl = chunk.IndexOf('\n', i);
            int segEnd = nl < 0 ? chunk.Length : nl + 1;
            var line = chunk.Substring(i, segEnd - i);
            i = segEnd;

            var cls = ConsoleLineClassifier.Classify(line);
            bool hasNewline = line.Length > 0 && line[^1] == '\n';
            if (cls.IsToolResult)
            {
                sb.Append("⎿ …");
                if (hasNewline) sb.Append('\n');
            }
            else if (cls.IsToolHeader)
            {
                var open = cls.Trimmed.IndexOf('(');
                if (open > 0)
                    sb.Append(cls.Trimmed, 0, open + 1).Append('…').Append(')');
                else
                    sb.Append(cls.Trimmed);
                if (hasNewline) sb.Append('\n');
            }
            else
            {
                sb.Append(line);
            }
        }
        return sb.ToString();
    }

    /// ASCII characters that nearly every styling regex depends on. A chunk
    /// with only letters/digits/whitespace can't possibly match any rule, so
    /// we skip the O(rules × length) regex loop entirely for that common
    /// case. Kept to ASCII for cheap scanning — the few Unicode-glyph rules
    /// (▸⎿🧠 etc.) always co-occur with ASCII structural chars, so we don't
    /// miss anything in practice.
    private static readonly System.Collections.Generic.HashSet<char> _stylingTriggerChars = new()
    {
        '[', ']', '{', '}', '(', ')', '<', '>', '/', '\\', '#', '@',
        '=', '!', '"', '*', ':', '|', '-', '+', '?'
    };

    private IEnumerable<(string text, StylingRule? rule)> Tokenize(string chunk)
    {
        var rules = _vm.Settings.StylingRules;
        if (rules.Count == 0 || string.IsNullOrEmpty(chunk))
        {
            yield return (chunk, null);
            yield break;
        }

        // Fast path: pure alphanumeric + whitespace text can't match any rule.
        // Saves 20-30 regex invocations on every plain-text streaming delta.
        if (IsPlainText(chunk))
        {
            yield return (chunk, null);
            yield break;
        }

        var startIdx = 0;
        while (startIdx < chunk.Length)
        {
            var nl = chunk.IndexOf('\n', startIdx);
            int segEnd = nl < 0 ? chunk.Length : nl + 1;
            var originalSeg = chunk.Substring(startIdx, segEnd - startIdx);
            startIdx = segEnd;
            if (originalSeg.Length == 0) break;

            var (segment, hard) = ApplyReplacements(originalSeg, rules);

            var styleSpans = new List<(int start, int len, StylingRule rule, int idx)>();
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (r.Replacement != null) continue;
                if (r.CompiledRegex is null) continue;
                foreach (Match m in r.CompiledRegex.Matches(segment))
                {
                    if (m.Length == 0) continue;
                    styleSpans.Add((m.Index, m.Length, r, i));
                }
            }
            styleSpans.Sort((a, b) =>
            {
                if (a.start != b.start) return a.start.CompareTo(b.start);
                if (a.len != b.len) return b.len.CompareTo(a.len);
                return a.idx.CompareTo(b.idx);
            });
            var acceptedStyle = new List<(int start, int len, StylingRule rule)>();
            int sCursor = 0;
            foreach (var s in styleSpans)
            {
                if (s.start < sCursor) continue;
                acceptedStyle.Add((s.start, s.len, s.rule));
                sCursor = s.start + s.len;
            }

            int pos = 0;
            int hIdx = 0;
            int stIdx = 0;
            while (pos < segment.Length)
            {
                while (hIdx < hard.Count && hard[hIdx].start + hard[hIdx].len <= pos) hIdx++;
                while (stIdx < acceptedStyle.Count && acceptedStyle[stIdx].start + acceptedStyle[stIdx].len <= pos) stIdx++;

                if (hIdx < hard.Count && hard[hIdx].start == pos)
                {
                    var h = hard[hIdx];
                    yield return (segment.Substring(h.start, h.len), h.rule);
                    pos = h.start + h.len;
                    continue;
                }

                int hardBoundary = hIdx < hard.Count ? hard[hIdx].start : segment.Length;

                if (stIdx < acceptedStyle.Count && acceptedStyle[stIdx].start <= pos)
                {
                    var s = acceptedStyle[stIdx];
                    int sEnd = Math.Min(s.start + s.len, hardBoundary);
                    yield return (segment.Substring(pos, sEnd - pos), s.rule);
                    pos = sEnd;
                    continue;
                }

                int nextStyleStart = stIdx < acceptedStyle.Count ? acceptedStyle[stIdx].start : segment.Length;
                int end = Math.Min(hardBoundary, nextStyleStart);
                if (end <= pos) break;
                yield return (segment.Substring(pos, end - pos), null);
                pos = end;
            }
        }
    }

    private static (string segment, List<(int start, int len, StylingRule rule)> hard)
        ApplyReplacements(string seg, IList<StylingRule> rules)
    {
        var matches = new List<(Match m, StylingRule rule, int idx)>();
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (r.Replacement == null || r.CompiledRegex == null) continue;
            foreach (Match m in r.CompiledRegex.Matches(seg))
            {
                if (m.Length == 0) continue;
                matches.Add((m, r, i));
            }
        }
        if (matches.Count == 0) return (seg, new List<(int, int, StylingRule)>());

        matches.Sort((a, b) =>
        {
            if (a.m.Index != b.m.Index) return a.m.Index.CompareTo(b.m.Index);
            if (a.m.Length != b.m.Length) return b.m.Length.CompareTo(a.m.Length);
            return a.idx.CompareTo(b.idx);
        });

        var sb = new System.Text.StringBuilder();
        var hard = new List<(int, int, StylingRule)>();
        int pos = 0;
        int cursor = 0;
        foreach (var mm in matches)
        {
            if (mm.m.Index < cursor) continue;
            if (mm.m.Index > pos) sb.Append(seg, pos, mm.m.Index - pos);
            var rep = mm.m.Result(mm.rule.Replacement!);
            int newStart = sb.Length;
            sb.Append(rep);
            hard.Add((newStart, rep.Length, mm.rule));
            pos = mm.m.Index + mm.m.Length;
            cursor = pos;
        }
        if (pos < seg.Length) sb.Append(seg, pos, seg.Length - pos);
        return (sb.ToString(), hard);
    }

    private void AttachMentionHighlightAdorner()
    {
        try
        {
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(PromptBox);
            if (layer == null) return;
            // Don't double-add.
            var existing = layer.GetAdorners(PromptBox);
            if (existing != null && existing.Any(a => a is MentionHighlightAdorner)) return;
            layer.Add(new MentionHighlightAdorner(PromptBox));
        }
        catch { }
    }

    // ---------- prompt @-mention ----------

    private FileMentionIndex? GetIndexForCurrentProject()
    {
        var dir = _vm.SelectedProject?.WorkingDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        if (!_mentionIndexes.TryGetValue(dir, out var idx))
        {
            idx = new FileMentionIndex(dir);
            _mentionIndexes[dir] = idx;
        }
        return idx;
    }

    private TextBox? _activeMentionBox;
    private bool _suppressMentionUpdate;

    private static (int start, int end, string token)? CurrentCaretToken(TextBox box)
    {
        var text = box.Text ?? "";
        var caret = box.CaretIndex;
        if (caret < 0 || caret > text.Length) return null;
        int start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        int end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (start, end, text.Substring(start, end - start));
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateMentionPopup(PromptBox);

    private void PromptBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateMentionPopup(PromptBox);
        AutoSelectMentionIfCaretInside(PromptBox);
    }

    /// If the caret lands inside an @ref token and nothing is yet selected,
    /// select the whole token so it behaves as an atomic chip (type/delete
    /// replaces the whole thing rather than editing the hidden full path).
    private bool _autoSelecting;
    private void AutoSelectMentionIfCaretInside(TextBox box)
    {
        if (_autoSelecting) return;
        if (box.SelectionLength > 0) return;
        var text = box.Text ?? "";
        if (string.IsNullOrEmpty(text)) return;
        var caret = box.CaretIndex;
        foreach (Match m in MentionRef.Matches(text))
        {
            if (caret > m.Index && caret < m.Index + m.Length)
            {
                _autoSelecting = true;
                try
                {
                    box.Select(m.Index, m.Length);
                }
                finally { _autoSelecting = false; }
                break;
            }
        }
    }

    private void PromptBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Popup's StaysOpen="False" already closes on outside-click;
        // this backs it up if focus moves via keyboard.
        if (_activeMentionBox == PromptBox && MentionPopup.IsOpen && !MentionList.IsKeyboardFocusWithin)
            CloseMentionPopup();
    }

    private void UpdateMentionPopup(TextBox box)
    {
        if (_suppressMentionUpdate) return;
        var tok = CurrentCaretToken(box);
        if (tok is null)
        {
            if (_activeMentionBox == box) CloseMentionPopup();
            return;
        }
        var (start, _, token) = tok.Value;
        // Only trigger when the token starts with '@' AND has at least one
        // character after it (codex parity).
        if (token.Length < 2 || token[0] != '@')
        {
            if (_activeMentionBox == box) CloseMentionPopup();
            return;
        }

        var query = token.Substring(1).Trim('"');
        var idx = GetIndexForCurrentProject();
        if (idx == null)
        {
            if (_activeMentionBox == box) CloseMentionPopup();
            return;
        }

        var results = idx.Search(query);
        if (results.Count == 0)
        {
            if (_activeMentionBox == box) CloseMentionPopup();
            return;
        }

        MentionList.ItemsSource = results;
        if (MentionList.SelectedIndex < 0) MentionList.SelectedIndex = 0;
        _mentionTokenStart = start;
        _activeMentionBox = box;
        MentionPopup.PlacementTarget = box;
        PositionMentionPopup();
        MentionPopup.IsOpen = true;
    }

    private void PositionMentionPopup()
    {
        var box = _activeMentionBox;
        if (box == null) return;
        try
        {
            var rect = box.GetRectFromCharacterIndex(_mentionTokenStart);
            if (rect.IsEmpty) return;
            MentionPopup.HorizontalOffset = rect.X;
            MentionPopup.VerticalOffset = rect.Y + rect.Height + 2;
        }
        catch { }
    }

    private void CloseMentionPopup()
    {
        if (MentionPopup.IsOpen) MentionPopup.IsOpen = false;
        _mentionTokenStart = -1;
        _activeMentionBox = null;
    }

    private void AcceptMentionSelection()
    {
        var box = _activeMentionBox;
        if (box == null) return;
        if (!MentionPopup.IsOpen) return;
        if (MentionList.SelectedItem is not string fullPath) return;
        if (_mentionTokenStart < 0) return;

        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;

        // Register the full path and use a short label in the visible prompt.
        var shortLabel = conv.RegisterMention(fullPath);

        var text = box.Text ?? "";
        int start = _mentionTokenStart;
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

        var insert = "@" + FileMentionIndex.QuoteIfNeeded(shortLabel) + " ";
        var newText = text.Substring(0, start) + insert + text.Substring(end);
        var newCaret = start + insert.Length;

        _suppressMentionUpdate = true;
        try
        {
            box.Text = newText;
            box.CaretIndex = newCaret;
        }
        finally { _suppressMentionUpdate = false; }

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        CloseMentionPopup();
    }

    private void PromptBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+Enter submits regardless of popup state — always takes
        // precedence so a pending @-mention popup doesn't swallow it.
        if (e.Key == System.Windows.Input.Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            CloseMentionPopup();
            // Flush the binding so the VM has the latest prompt before running.
            PromptBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            StartStop_Click(this, new RoutedEventArgs());
            return;
        }

        if (_activeMentionBox == PromptBox && MentionPopup.IsOpen)
            HandleMentionPopupKey(e);
    }

    /// Shared Up/Down/Tab/Enter/Escape navigation for the mention popup.
    private void HandleMentionPopupKey(System.Windows.Input.KeyEventArgs e)
    {
        int count = MentionList.Items.Count;
        if (count == 0) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Up:
                MentionList.SelectedIndex = (MentionList.SelectedIndex - 1 + count) % count;
                MentionList.ScrollIntoView(MentionList.SelectedItem);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down:
                MentionList.SelectedIndex = (MentionList.SelectedIndex + 1) % count;
                MentionList.ScrollIntoView(MentionList.SelectedItem);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Tab:
            case System.Windows.Input.Key.Enter:
                AcceptMentionSelection();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                CloseMentionPopup();
                e.Handled = true;
                break;
        }
    }

    // ---------- hover tooltip for @refs ----------

    private int _lastTooltipCharIndex = -1;

    private void PromptBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => ShowMentionTooltipAt(PromptBox, e.GetPosition(PromptBox));

    private void PromptBox_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        HideMentionTooltip();

    private void ShowMentionTooltipAt(TextBox box, System.Windows.Point pt)
    {
        try
        {
            int ci = box.GetCharacterIndexFromPoint(pt, snapToText: false);
            if (ci < 0 || ci > (box.Text?.Length ?? 0))
            {
                HideMentionTooltip();
                return;
            }
            var text = box.Text ?? "";
            Match? hit = null;
            foreach (Match m in MentionRef.Matches(text))
            {
                if (ci >= m.Index && ci <= m.Index + m.Length - 1)
                {
                    hit = m;
                    break;
                }
            }
            if (hit == null)
            {
                HideMentionTooltip();
                return;
            }
            var label = hit.Groups[1].Success ? hit.Groups[1].Value : hit.Groups[2].Value;
            var conv = _vm.SelectedProject?.SelectedConversation;
            string display = label;
            if (conv != null && conv.TryGetMentionFullPath(label, out var full))
                display = full;
            if (hit.Index == _lastTooltipCharIndex && MentionTooltipPopup.IsOpen && MentionTooltipPopup.PlacementTarget == box) return;
            _lastTooltipCharIndex = hit.Index;

            MentionTooltipText.Text = display;
            MentionTooltipPopup.PlacementTarget = box;
            var rect = box.GetRectFromCharacterIndex(hit.Index);
            if (!rect.IsEmpty)
            {
                MentionTooltipPopup.HorizontalOffset = rect.X;
                MentionTooltipPopup.VerticalOffset = rect.Y - 26;
            }
            MentionTooltipPopup.IsOpen = true;
        }
        catch { HideMentionTooltip(); }
    }

    private void HideMentionTooltip()
    {
        _lastTooltipCharIndex = -1;
        if (MentionTooltipPopup.IsOpen) MentionTooltipPopup.IsOpen = false;
    }

    // ---------- commands ----------

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var menu = BuildProjectMenu(btn);
        menu.IsOpen = true;
    }

    private ContextMenu BuildProjectMenu(Button btn)
    {
        var entries = _vm.GetRecentWorkspaceEntries();
        var open = entries.Where(e => e.IsOpen).ToList();
        var recent = entries.Where(e => !e.IsOpen && !e.IsMissing).Take(8).ToList();
        var missing = entries.Where(e => !e.IsOpen && e.IsMissing).ToList();

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HasDropShadow = true,
        };

        if (open.Count > 0)
        {
            AppendProjectMenuSection(menu, $"Open workspaces ({open.Count})");
            foreach (var entry in open)
                menu.Items.Add(BuildWorkspaceMenuItem(entry, markMissing: false));
        }

        if (recent.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            AppendProjectMenuSection(menu, $"Recent workspaces ({recent.Count})");
            foreach (var entry in recent)
                menu.Items.Add(BuildWorkspaceMenuItem(entry, markMissing: false));
        }

        if (missing.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            AppendProjectMenuSection(menu, $"Missing paths ({missing.Count})");
            foreach (var entry in missing)
                menu.Items.Add(BuildWorkspaceMenuItem(entry, markMissing: true));

            var forgetAll = new MenuItem { Header = $"Forget all missing ({missing.Count})" };
            forgetAll.Click += (_, _) => _vm.RemoveMissingRecentWorkspaces();
            menu.Items.Add(forgetAll);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());

        var openNew = new MenuItem { Header = "Open new workspace…" };
        openNew.Click += (_, _) => OpenFolderPickerAndAdd();
        menu.Items.Add(openNew);

        return menu;
    }

    private static void AppendProjectMenuSection(ContextMenu menu, string title)
    {
        menu.Items.Add(new MenuItem
        {
            Header = title,
            IsEnabled = false,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.75,
        });
    }

    private MenuItem BuildWorkspaceMenuItem(MainViewModel.RecentWorkspaceEntry entry, bool markMissing)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children =
            {
                new TextBlock
                {
                    Text = entry.IsSelected ? $"{entry.DisplayName} (current)" : entry.DisplayName,
                    FontWeight = FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = entry.Path,
                    Opacity = 0.6,
                    FontSize = 10,
                },
            },
        };

        if (!markMissing)
        {
            var item = new MenuItem { Tag = entry.Path, Header = header };
            item.Click += (_, _) =>
            {
                if (item.Tag is string path) _vm.AddProject(path);
            };
            return item;
        }

        var missing = new MenuItem { Tag = entry.Path, Header = header };
        missing.Items.Add(new MenuItem
        {
            Header = "Forget missing path",
        });
        if (missing.Items[0] is MenuItem forget)
        {
            forget.Click += (_, _) =>
            {
                if (missing.Tag is string path) _vm.RemoveRecentWorkspace(path);
            };
        }
        return missing;
    }

    private void OpenFolderPickerAndAdd()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Add project — select working directory",
            InitialDirectory = _vm.SelectedProject?.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };
        if (dlg.ShowDialog(this) == true)
            _vm.AddProject(dlg.FolderName);
    }

    private void TaskPreviewFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (TasksTabs == null) return;
        if (TasksTabs.SelectedIndex != 1)
            TasksTabs.SelectedIndex = 1;
        QueueScrollTasks();
    }

    private void ToolbarTerminal_Click(object sender, RoutedEventArgs e)
    {
        var projectDir = _vm.SelectedProject?.WorkingDirectory;
        ShellPathActions.OpenTerminalHere(projectDir);
        e.Handled = true;
    }

    private void PromptPath_Click(object sender, RoutedEventArgs e)
    {
        var promptFile = _vm.SelectedProject?.SelectedConversation?.PromptFile;
        ShellPathActions.CopyPath(promptFile);
        e.Handled = true;
    }

    private void TasksPath_Click(object sender, RoutedEventArgs e)
    {
        var tasksFile = _vm.SelectedProject?.SelectedConversation?.TasksFile;
        ShellPathActions.CopyPath(tasksFile);
        e.Handled = true;
    }

    // ---- inline rename of conversation ----

    private void ConvName_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is TextBlock tb && tb.DataContext is ConversationViewModel c)
        {
            c.BeginRename();
            e.Handled = true;
        }
    }

    private void ConvNameEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ConvNameEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            (tb.DataContext as ConversationViewModel)?.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            (tb.DataContext as ConversationViewModel)?.CancelRename();
            e.Handled = true;
        }
    }

    private void ConvNameEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ConversationViewModel c && c.IsEditingName)
            c.CommitRename();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ProjectViewModel p)
            _vm.CloseProject(p);
        e.Handled = true;
    }

    private void AddConversation_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedProject?.AddConversation();
    }

    private async void RefreshConversationTitles_Click(object sender, RoutedEventArgs e)
    {
        var project = _vm.SelectedProject;
        if (project == null) return;
        if (string.IsNullOrWhiteSpace(_vm.OpenRouterApiKey))
        {
            ShowOpenRouterNamingDialog();
            if (string.IsNullOrWhiteSpace(_vm.OpenRouterApiKey)) return;
        }

        try { await project.RefreshConversationTitlesAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Refresh conversation titles", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearConversationFilter_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedProject?.ClearConversationFilter();
        ConversationFilterBox.Focus();
    }

    private void ConversationFilterBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            _vm.SelectedProject?.ClearConversationFilter();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Down)
        {
            // Quality-of-life: Down arrow from the filter box drops focus
            // into the (filtered) conversation list so the user can arrow
            // through matches without reaching for the mouse.
            ConversationList.Focus();
            e.Handled = true;
        }
    }

    private void ImportSession_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProject == null) return;
        ImportSessionInput.Text = "";
        OpenPopupWithFocus(ImportSessionPopup, () =>
        {
            ImportSessionInput.Focus();
            ImportSessionInput.SelectAll();
        });
    }

    private void ImportSessionUse_Click(object sender, RoutedEventArgs e)
    {
        var sid = (ImportSessionInput.Text ?? "").Trim();
        if (sid.Length == 0) return;
        var project = _vm.SelectedProject;
        if (project == null) return;
        project.ImportConversationWithSession(sid);
        ImportSessionPopup.IsOpen = false;
    }

    private void ImportSessionCancel_Click(object sender, RoutedEventArgs e)
    {
        ImportSessionPopup.IsOpen = false;
    }

    private void ImportSessionInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            ImportSessionUse_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            ImportSessionPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void RemoveConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ConversationViewModel c)
        {
            var project = _vm.Projects.FirstOrDefault(p => p.Conversations.Contains(c));
            project?.RemoveConversation(c);
        }
        e.Handled = true;
    }

    // ---- Conversation right-click context menu ----

    /// The ListBoxItem's ContextMenu inherits DataContext from the item, so
    /// each MenuItem.DataContext is the conversation the user right-clicked.
    private static ConversationViewModel? ResolveContextConv(object sender)
        => sender is MenuItem mi ? mi.DataContext as ConversationViewModel : null;

    // ---- Recent-commit right-click context menu ----

    /// Same DataContext-inheritance trick as the conversation row menu:
    /// the commit Border's ContextMenu places each MenuItem inside the
    /// commit's DataContext, so we can resolve the clicked GitCommit off
    /// the sender's DataContext without tracking PlacementTarget manually.
    private static Services.GitCommit? ResolveContextCommit(object sender)
        => sender is MenuItem mi ? mi.DataContext as Services.GitCommit : null;

    private void CopyCommitHash_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextCommit(sender) is { } c) ShellPathActions.CopyText(c.Hash);
    }

    private void CopyCommitShortHash_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextCommit(sender) is { } c) ShellPathActions.CopyText(c.ShortHash);
    }

    private void CopyCommitSubject_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextCommit(sender) is { } c) ShellPathActions.CopyText(c.Subject);
    }

    private void CopyCommitHashSubject_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextCommit(sender) is { } c)
            ShellPathActions.CopyText($"{c.ShortHash} {c.Subject}");
    }

    private void ForkConversation_Click(object sender, RoutedEventArgs e)
    {
        var src = ResolveContextConv(sender);
        if (src == null) return;
        var project = _vm.Projects.FirstOrDefault(p => p.Conversations.Contains(src));
        project?.ForkConversation(src);
    }

    private void RenameConversationCtx_Click(object sender, RoutedEventArgs e)
    {
        var c = ResolveContextConv(sender);
        c?.BeginRename();
    }

    private void DeleteConversationCtx_Click(object sender, RoutedEventArgs e)
    {
        var c = ResolveContextConv(sender);
        if (c == null) return;
        var project = _vm.Projects.FirstOrDefault(p => p.Conversations.Contains(c));
        project?.RemoveConversation(c);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null) return;
        try
        {
            await c.ToggleStartStopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "JustCode error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        conv.ClearSession();
    }

    private void CopyConsole_Click(object sender, RoutedEventArgs e)
    {
        var box = GetActiveConsoleBox();
        var doc = box?.Document;
        if (doc == null) return;
        var range = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd);
        Services.ShellPathActions.CopyText(range.Text);
    }

    private void CopyPinned_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null || !conv.HasPinnedText) return;
        Services.ShellPathActions.CopyText(conv.PinnedText);
    }

    private void SessionPill_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        var id = conv?.CurrentSessionId;
        if (string.IsNullOrEmpty(id) || conv is null) return;
        Services.ShellPathActions.CopyText(id);
        conv.FlashSessionCopied();
        e.Handled = true;
    }

    private void ModelPickerButton_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        // Retarget the shared picker to the current conversation's tool so the
        // model list + favorites match the agent being used.
        _vm.ModelPicker.Tool = conv.Tool;
        // For Pi, if the cache is empty (first run, or previous parse failed)
        // kick off a refresh immediately so the user sees real models instead
        // of the hardcoded fallback seed (which contains openai/gpt-5 etc.
        // that don't work under GitHub Copilot auth).
        if (conv.Tool == JustCode.Models.CliTool.Pi &&
            (_vm.Settings.PiModelCache == null || _vm.Settings.PiModelCache.Count == 0))
        {
            _ = _vm.ModelPicker.RefreshFromCliAsync();
        }
        OpenPopupWithFocus(ModelPopup, () =>
        {
            ModelSearchBox.Focus();
            ModelSearchBox.SelectAll();
        });
    }

    private void ModelRefresh_Click(object sender, RoutedEventArgs e)
        => _ = _vm.ModelPicker.RefreshFromCliAsync(clearFirst: true);

    private void ModelClear_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        conv.ModelText = "";
        ModelPopup.IsOpen = false;
    }

    private void ModelFavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ModelEntry entry)
            entry.ToggleFavorite();
        e.Handled = true;
    }

    private void ModelList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AcceptModelSelection();

    private void ModelList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { AcceptModelSelection(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { ModelPopup.IsOpen = false; e.Handled = true; }
    }

    private void ModelSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Down && ModelList.Items.Count > 0)
        {
            ModelList.SelectedIndex = 0;
            var container = ModelList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            container?.Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && ModelList.Items.Count > 0)
        {
            ModelList.SelectedIndex = 0;
            AcceptModelSelection();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            ModelPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void AcceptModelSelection()
    {
        if (ModelList.SelectedItem is not ModelEntry entry) return;
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        conv.ModelText = entry.Name;
        ModelPopup.IsOpen = false;
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.SelectedProject?.SelectedConversation?.EnqueueChat();
    }

    private async void ChatPrimary_Click(object sender, RoutedEventArgs e)
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null) return;
        ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (c.IsPrimaryActionStart)
        {
            try { await c.ToggleStartStopAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "JustCode error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        c.EnqueueChat();
    }

    private async void ChatStop_Click(object sender, RoutedEventArgs e)
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null || !c.IsRunning) return;
        try { await c.ToggleStartStopAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "JustCode error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChatInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMentionPopup(ChatInputBox);
        UpdateSlashPopup();
    }

    private void ChatInputBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateMentionPopup(ChatInputBox);
        AutoSelectMentionIfCaretInside(ChatInputBox);
        UpdateSlashPopup();
    }

    private void ChatInputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_activeMentionBox == ChatInputBox && MentionPopup.IsOpen && !MentionList.IsKeyboardFocusWithin)
            CloseMentionPopup();
        if (SlashPopup.IsOpen && !SlashList.IsKeyboardFocusWithin)
            CloseSlashPopup();
    }

    private void ChatInputBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => ShowMentionTooltipAt(ChatInputBox, e.GetPosition(ChatInputBox));

    private void ChatInputBox_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        HideMentionTooltip();

    private void ChatInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Slash-command popup takes the highest precedence when it's open,
        // so arrow keys / Tab / Enter drive it instead of submitting.
        if (SlashPopup.IsOpen)
        {
            HandleSlashPopupKey(e);
            if (e.Handled) return;
        }

        // Mention popup navigation takes precedence when open on this box.
        if (_activeMentionBox == ChatInputBox && MentionPopup.IsOpen)
        {
            HandleMentionPopupKey(e);
            if (e.Handled) return;
        }

        var conv = _vm.SelectedProject?.SelectedConversation;

        // Up on the first line recalls queued items (newest → oldest). Down on
        // the last line steps forward; past the newest restores the draft.
        if (conv != null && e.Key == System.Windows.Input.Key.Up && IsCaretOnFirstLine(ChatInputBox))
        {
            if (conv.QueuedMessages.Count > 0 || conv.IsRecallingQueued)
            {
                e.Handled = true;
                ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                conv.RecallQueuedPrev();
                MoveChatCaretToEndDeferred();
                return;
            }
        }
        if (conv != null && e.Key == System.Windows.Input.Key.Down &&
            conv.IsRecallingQueued && IsCaretOnLastLine(ChatInputBox))
        {
            e.Handled = true;
            ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            conv.RecallQueuedNext();
            MoveChatCaretToEndDeferred();
            return;
        }

        // Alt+Up / Alt+Down reorder the chip that's currently loaded in
        // recall — lets the user shuffle without taking their hands off the
        // keyboard. Plain Alt so it doesn't clash with the Ctrl+Up/Down
        // caret-word navigation baked into TextBox.
        if (conv is { IsRecallingQueued: true } &&
            (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down) &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0 &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            int delta = e.Key == System.Windows.Input.Key.Up ? -1 : +1;
            if (conv.MoveRecalledQueued(delta))
            {
                e.Handled = true;
                return;
            }
        }

        // Escape exits recall mode (restores the saved draft).
        if (e.Key == System.Windows.Input.Key.Escape && conv is { IsRecallingQueued: true })
        {
            e.Handled = true;
            ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            conv.ExitRecallMode();
            MoveChatCaretToEndDeferred();
            return;
        }

        // Enter (no shift) sends (draft mode) or exits recall (edit mode).
        // Shift+Enter inserts newline (default behaviour).
        if (e.Key == System.Windows.Input.Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            CloseMentionPopup();
            ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            _vm.SelectedProject?.SelectedConversation?.EnqueueChat();
        }
    }

    private static bool IsCaretOnFirstLine(TextBox box)
    {
        int line = box.GetLineIndexFromCharacterIndex(box.CaretIndex);
        return line <= 0;
    }

    private static bool IsCaretOnLastLine(TextBox box)
    {
        int endIdx = box.Text?.Length ?? 0;
        int endLine = box.GetLineIndexFromCharacterIndex(endIdx);
        int curLine = box.GetLineIndexFromCharacterIndex(box.CaretIndex);
        return curLine >= endLine;
    }

    /// Caret must be set after WPF flushes the binding into the TextBox.
    private void MoveChatCaretToEndDeferred()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ChatInputBox.CaretIndex = ChatInputBox.Text?.Length ?? 0;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// Every queue-chip button / text row bound in XAML carries its chip as
    /// `Tag="{Binding}"` so the click handler can round-trip through the
    /// DataTemplate. This helper resolves both sender shapes (Button, TextBlock)
    /// and the current conversation in one shot; click handlers read as pure
    /// verb + action now, without the repeated `is Button b && b.Tag is …` bag.
    private bool TryQueuedChipAction(object sender,
        out ConversationViewModel conv, out QueuedChatMessage msg)
    {
        conv = null!;
        msg = null!;
        var tag = sender switch
        {
            FrameworkElement fe => fe.Tag,
            _ => null,
        };
        if (tag is not QueuedChatMessage m) return false;
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null) return false;
        conv = c;
        msg = m;
        return true;
    }

    private void RemoveQueued_Click(object sender, RoutedEventArgs e)
    {
        if (TryQueuedChipAction(sender, out var conv, out var msg))
            conv.RemoveQueued(msg);
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedProject?.SelectedConversation?.ClearQueue();
    }

    private void CopyQueued_Click(object sender, RoutedEventArgs e)
    {
        if (TryQueuedChipAction(sender, out _, out var msg)
            && !string.IsNullOrEmpty(msg.Text))
        {
            Services.ShellPathActions.CopyText(msg.Text);
        }
    }

    private void DuplicateQueued_Click(object sender, RoutedEventArgs e)
    {
        if (TryQueuedChipAction(sender, out var conv, out var msg))
            conv.DuplicateQueued(msg);
    }

    private void MoveQueuedUp_Click(object sender, RoutedEventArgs e)
    {
        if (TryQueuedChipAction(sender, out var conv, out var msg))
            conv.MoveQueuedUp(msg);
    }

    private void MoveQueuedDown_Click(object sender, RoutedEventArgs e)
    {
        if (TryQueuedChipAction(sender, out var conv, out var msg))
            conv.MoveQueuedDown(msg);
    }

    private void QueuedChipText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TryQueuedChipAction(sender, out var conv, out var msg))
            conv.BeginEditQueued(msg);
    }

    // ---------- Slash commands (queue prompt area) ----------

    /// Slash commands available in the chat box. Each entry is matched by
    /// substring against the user's query (the text after the leading `/`).
    /// Keep this sorted by likely usefulness; the first match auto-highlights.
    private sealed record SlashCommand(string Trigger, string Description);
    private static readonly IReadOnlyList<SlashCommand> AllSlashCommands = new[]
    {
        new SlashCommand("/plan",
            "Read and update the PLAN file for this conversation. Strict: no task execution."),
    };

    private void UpdateSlashPopup()
    {
        var text = ChatInputBox.Text ?? "";
        if (text.Length == 0 || text[0] != '/')
        {
            CloseSlashPopup();
            return;
        }

        // Only match on the first token so we don't keep the popup open for
        // long multi-line prompts that happen to start with `/`.
        var firstWhitespace = 0;
        while (firstWhitespace < text.Length && !char.IsWhiteSpace(text[firstWhitespace]))
            firstWhitespace++;
        if (ChatInputBox.CaretIndex > firstWhitespace)
        {
            CloseSlashPopup();
            return;
        }

        var query = text.Substring(1, firstWhitespace - 1);
        var matches = AllSlashCommands
            .Where(c => c.Trigger.Length > 1 &&
                        c.Trigger.Substring(1).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            CloseSlashPopup();
            return;
        }

        SlashList.ItemsSource = matches;
        if (SlashList.SelectedIndex < 0) SlashList.SelectedIndex = 0;
        SlashPopup.IsOpen = true;
    }

    private void CloseSlashPopup()
    {
        if (SlashPopup.IsOpen) SlashPopup.IsOpen = false;
    }

    private void HandleSlashPopupKey(System.Windows.Input.KeyEventArgs e)
    {
        int count = SlashList.Items.Count;
        if (count == 0) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Up:
                SlashList.SelectedIndex = (SlashList.SelectedIndex - 1 + count) % count;
                SlashList.ScrollIntoView(SlashList.SelectedItem);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down:
                SlashList.SelectedIndex = (SlashList.SelectedIndex + 1) % count;
                SlashList.ScrollIntoView(SlashList.SelectedItem);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Tab:
            case System.Windows.Input.Key.Enter:
                AcceptSlashSelection();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                CloseSlashPopup();
                e.Handled = true;
                break;
        }
    }

    private void AcceptSlashSelection()
    {
        if (!SlashPopup.IsOpen) return;
        if (SlashList.SelectedItem is not SlashCommand cmd) return;
        var current = ChatInputBox.Text ?? "";
        var firstWhitespace = 0;
        while (firstWhitespace < current.Length && !char.IsWhiteSpace(current[firstWhitespace]))
            firstWhitespace++;
        var newText = cmd.Trigger + " " + current.Substring(firstWhitespace).TrimStart();
        ChatInputBox.Text = newText;
        ChatInputBox.CaretIndex = newText.Length;
        ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        CloseSlashPopup();
    }

    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var naming = new MenuItem { Header = $"OpenRouter naming ({_vm.OpenRouterApiKeyStatus})..." };
        naming.Click += (_, _) => ShowOpenRouterNamingDialog();
        menu.Items.Add(naming);

        menu.Items.Add(new Separator());

        var config = new MenuItem { Header = "Open looper.conf" };
        config.Click += (_, _) => _vm.OpenConfigInNotepad();
        menu.Items.Add(config);

        menu.IsOpen = true;
    }

    private void ShowOpenRouterNamingDialog()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "OpenRouter Naming",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 234, 234)),
        };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = "Conversation tab names are generated through OpenRouter. The key is stored in the app config file.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(168, 168, 172)),
            Margin = new Thickness(0, 0, 0, 14),
        };
        Grid.SetRow(intro, 0);
        root.Children.Add(intro);

        var keyPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        keyPanel.Children.Add(DialogLabel("API KEY"));
        var keyBox = new PasswordBox
        {
            Password = _vm.OpenRouterApiKey,
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(27, 27, 28)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 234, 234)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            Padding = new Thickness(7, 4, 7, 4),
        };
        keyPanel.Children.Add(keyBox);
        Grid.SetRow(keyPanel, 1);
        root.Children.Add(keyPanel);

        var modelPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        modelPanel.Children.Add(DialogLabel("MODEL CODE NAME"));
        var modelBox = new ComboBox
        {
            IsEditable = true,
            Height = 30,
            Text = _vm.OpenRouterTitleModel,
            Background = new SolidColorBrush(Color.FromRgb(27, 27, 28)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 234, 234)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            Padding = new Thickness(7, 2, 7, 2),
        };
        modelBox.Items.Add(Models.LoopSettings.DefaultOpenRouterTitleModel);
        modelPanel.Children.Add(modelBox);
        Grid.SetRow(modelPanel, 2);
        root.Children.Add(modelPanel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 5, 10, 5),
            IsCancel = true,
        };
        var save = new Button
        {
            Content = "Save",
            MinWidth = 76,
            Padding = new Thickness(10, 5, 10, 5),
            IsDefault = true,
        };
        save.Click += (_, _) =>
        {
            _vm.OpenRouterApiKey = keyBox.Password;
            _vm.OpenRouterTitleModel = modelBox.Text;
            _vm.SaveConfig();
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();
    }

    private static TextBlock DialogLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(118, 118, 124)),
        Margin = new Thickness(0, 0, 0, 5),
    };

    // ---------- Sidebar tabs (VS Code-style activity bar) ----------

    private void SidebarTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        var project = _vm.SelectedProject;
        if (project == null) return;
        project.SidebarTab = tag;
        UpdateActivityBarStyles();
    }

    /// Apply the "active" style to whichever activity-bar button matches the
    /// current project's SidebarTab. Doing this in code-behind beats the
    /// MultiDataTrigger + per-button Opacity binding we had before: that path
    /// re-evaluated across every button on every ProjectViewModel PropertyChanged
    /// pulse, which showed up as visible jank when toggling tabs.
    private void UpdateActivityBarStyles()
    {
        var active = _vm.SelectedProject?.SidebarTab ?? "conversations";
        var baseStyle = (Style)FindResource("ActivityBarButton");
        var activeStyle = (Style)FindResource("ActivityBarButtonActive");
        if (TabFilesButton != null)
            TabFilesButton.Style = active == "files" ? activeStyle : baseStyle;
        if (TabConversationsButton != null)
            TabConversationsButton.Style = active == "conversations" ? activeStyle : baseStyle;
        if (TabGitButton != null)
            TabGitButton.Style = active == "git" ? activeStyle : baseStyle;
    }

    private void FilesRefresh_Click(object sender, RoutedEventArgs e)
    {
        var fx = _vm.SelectedProject?.FileExplorer;
        if (fx == null) return;
        // Default refresh keeps expansion state; Shift-click does a hard reset
        // (fully wipe + rebuild) for the rare case where the tree has drifted.
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            fx.HardRefresh();
        else
            fx.Refresh();
    }

    private void FilesCollapseAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedProject?.FileExplorer?.CollapseAll();
    }

    private void FileNode_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement fe && fe.DataContext is FileNode node && !node.IsDirectory)
        {
            FileExplorerViewModel.Open(node);
            e.Handled = true;
        }
    }

    private FileNode? GetContextFileNode(object sender)
    {
        // ContextMenu is shared on the TreeView (not per-TreeViewItem) for
        // perf, so the clicked FileNode has to come from TreeView.SelectedItem.
        // Right-click selects the target via FileTree_PreviewMouseRightButtonDown.
        if (FileTree?.SelectedItem is FileNode n) return n;
        return null;
    }

    /// Walk up the visual tree from the right-clicked element to the
    /// TreeViewItem and select it. WPF doesn't auto-select on right-click,
    /// so we'd otherwise open the menu against whatever item was already
    /// selected (usually the wrong one).
    private void FileTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not TreeViewItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is TreeViewItem tvi) tvi.IsSelected = true;
    }

    private void FileNodeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFileNode(sender) is { } n) FileExplorerViewModel.Open(n);
    }

    private void FileNodeReveal_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFileNode(sender) is { } n) FileExplorerViewModel.RevealInExplorer(n);
    }

    private void FileNodeCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFileNode(sender) is { } n) FileExplorerViewModel.CopyPath(n);
    }

    private void FileNodeCopyRelPath_Click(object sender, RoutedEventArgs e)
    {
        var n = GetContextFileNode(sender);
        var wd = _vm.SelectedProject?.WorkingDirectory;
        if (n == null || string.IsNullOrEmpty(wd)) return;
        FileExplorerViewModel.CopyRelativePath(n, wd);
    }

    private void FileNodeCopyFileName_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFileNode(sender) is { } n) FileExplorerViewModel.CopyFileName(n);
    }

    private void FileNodeOpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFileNode(sender) is { } n) FileExplorerViewModel.OpenTerminalHere(n);
    }

    private void FileNodeCopyContent_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextFileNode(sender) is { } n) FileExplorerViewModel.CopyFileContent(n);
    }

    // ---------- Git panel ----------

    private GitViewModel? Git => _vm.SelectedProject?.Git;

    private async void GitRefresh_Click(object sender, RoutedEventArgs e)
        { if (Git != null) await Git.RefreshAsync(); }
    private async void GitFetch_Click(object sender, RoutedEventArgs e)
        { if (Git != null) await Git.FetchAsync(); }
    private async void GitPull_Click(object sender, RoutedEventArgs e)
        { if (Git != null) await Git.PullAsync(); }
    private async void GitPush_Click(object sender, RoutedEventArgs e)
        { if (Git != null) await Git.PushAsync(); }

    private async void GitStage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is GitFileRow row && Git != null)
        { await Git.StageAsync(row); e.Handled = true; }
    }
    private async void GitUnstage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is GitFileRow row && Git != null)
        { await Git.UnstageAsync(row); e.Handled = true; }
    }
    private async void GitDiscard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not GitFileRow row || Git == null) return;
        var confirm = MessageBox.Show(this,
            $"Discard changes to {row.FullPath}?\n\nThis cannot be undone.",
            "Discard changes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) { e.Handled = true; return; }
        await Git.DiscardAsync(row); e.Handled = true;
    }

    // ---- Git file row right-click context menu ----

    /// Same DataContext-inheritance trick as the conversation/commit row menus:
    /// MenuItems inside the Border's ContextMenu inherit the row's DataContext,
    /// so each click resolves straight to a `GitFileRow` without poking at
    /// PlacementTarget.
    private static GitFileRow? ResolveContextGitFileRow(object sender)
        => sender is MenuItem mi ? mi.DataContext as GitFileRow : null;

    private string? AbsoluteGitRowPath(GitFileRow row)
    {
        var wd = _vm.SelectedProject?.WorkingDirectory;
        if (string.IsNullOrEmpty(wd) || string.IsNullOrEmpty(row.FullPath)) return null;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(wd, row.FullPath));
    }

    private void GitFileRowOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextGitFileRow(sender) is { } row &&
            AbsoluteGitRowPath(row) is { } abs)
            ShellPathActions.Open(abs);
    }

    private void GitFileRowReveal_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextGitFileRow(sender) is { } row &&
            AbsoluteGitRowPath(row) is { } abs)
            ShellPathActions.RevealInExplorer(abs);
    }

    private void GitFileRowCopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextGitFileRow(sender) is { } row &&
            AbsoluteGitRowPath(row) is { } abs)
            ShellPathActions.CopyText(abs);
    }

    private void GitFileRowCopyRelPath_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextGitFileRow(sender) is { } row)
            ShellPathActions.CopyText(row.FullPath.Replace('\\', '/'));
    }

    private void GitFileRowCopyFileName_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveContextGitFileRow(sender) is { } row)
            ShellPathActions.CopyText(row.FileName);
    }
    private async void GitStageAll_Click(object sender, RoutedEventArgs e)
        { if (Git != null) await Git.StageAllAsync(); }
    private async void GitUnstageAll_Click(object sender, RoutedEventArgs e)
        { if (Git != null) await Git.UnstageAllAsync(); }

    private async void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        GitCommitBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Git != null) await Git.CommitAsync();
    }
    private async void GitAmend_Click(object sender, RoutedEventArgs e)
    {
        GitCommitBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Git != null) await Git.AmendAsync();
    }
    private async void GitStash_Click(object sender, RoutedEventArgs e)
    {
        GitCommitBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Git != null) await Git.StashAsync();
    }

    private async void GitAiMessage_Click(object sender, RoutedEventArgs e)
    {
        if (Git != null) await Git.GenerateMessageWithAIAsync();
    }

    private void GitMoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.ContextMenu == null) return;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async void GitCommitBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            GitCommitBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (Git != null) await Git.CommitAsync();
        }
    }

    private void GitDismissError_Click(object sender, RoutedEventArgs e)
    {
        // Setter is private on the VM; easiest: kick a refresh which clears
        // the error on success. Failing that, leave the banner — user can
        // click the × again after the next successful action.
        _ = Git?.RefreshAsync();
    }

    private void GitBranch_Click(object sender, RoutedEventArgs e)
    {
        if (Git == null) return;
        GitBranchList.SelectedItem = Git.CurrentBranch;
        OpenPopupWithFocus(GitBranchPopup, () =>
        {
            if (GitBranchList.SelectedItem != null)
                GitBranchList.ScrollIntoView(GitBranchList.SelectedItem);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void GitBranchList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GitBranchList.SelectedItem is string branch && Git != null)
        {
            GitBranchPopup.IsOpen = false;
            await Git.CheckoutAsync(branch);
        }
    }

    private async void GitCreateBranch_Click(object sender, RoutedEventArgs e)
    {
        if (Git == null) return;
        GitBranchPopup.IsOpen = false;
        var dlg = new SkillNameDialog
        {
            Owner = this,
            Title = "New branch",
            FieldLabelText = "Branch name",
            HintTextValue = "Creates and checks out the new branch from HEAD.",
        };
        if (dlg.ShowDialog() != true) return;
        await Git.CreateBranchAsync(dlg.SkillName);
    }

    private async void GitStagedRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsClickInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is FrameworkElement fe && fe.DataContext is GitFileRow row)
            await ShowGitDiffAsync(row);
    }

    private static bool IsClickInsideButton(DependencyObject? origin)
    {
        while (origin != null)
        {
            if (origin is Button) return true;
            origin = origin switch
            {
                FrameworkContentElement fce => fce.Parent,
                Visual => VisualTreeHelper.GetParent(origin),
                _ => null,
            };
        }
        return false;
    }

    private async Task ShowGitDiffAsync(GitFileRow row)
    {
        var wd = _vm.SelectedProject?.WorkingDirectory;
        if (string.IsNullOrEmpty(wd)) return;

        string diff = "";
        try
        {
            diff = await GitService.DiffAsync(wd, row.FullPath, row.IsStagedGroup);
            if (string.IsNullOrWhiteSpace(diff))
                diff = await BuildSyntheticDiffIfNeededAsync(wd, row);
        }
        catch { }

        if (string.IsNullOrWhiteSpace(diff))
            diff = $"No textual diff available for {row.FullPath}.\n\nThis can happen for binary files, empty diffs, or some Git states.";

        var fullPath = Path.Combine(wd, row.FullPath);
        InlineGitDiffTitle.Text = row.FullPath;
        InlineGitDiffViewer.SetDiff(diff, fullPath);
        ConversationPromptPane.Visibility = Visibility.Collapsed;
        ConversationPaneSplitter.Visibility = Visibility.Collapsed;
        ConversationTasksPane.Visibility = Visibility.Collapsed;
        InlineGitDiffPanel.Visibility = Visibility.Visible;
    }

    private void InlineGitDiffClose_Click(object sender, RoutedEventArgs e)
    {
        CloseInlineGitDiff();
    }

    private void CloseInlineGitDiff()
    {
        if (InlineGitDiffPanel == null) return;
        InlineGitDiffPanel.Visibility = Visibility.Collapsed;
        if (ConversationPromptPane != null) ConversationPromptPane.Visibility = Visibility.Visible;
        if (ConversationPaneSplitter != null) ConversationPaneSplitter.Visibility = Visibility.Visible;
        if (ConversationTasksPane != null) ConversationTasksPane.Visibility = Visibility.Visible;
    }

    private static async Task<string> BuildSyntheticDiffIfNeededAsync(string wd, GitFileRow row)
    {
        bool likelyUntracked = (!row.IsStagedGroup && row.Change.WorkingKind == GitChangeKind.Untracked)
                              || (row.IsStagedGroup && row.Change.IndexKind == GitChangeKind.Untracked);
        if (!likelyUntracked) return "";

        var full = Path.Combine(wd, row.FullPath);
        if (!File.Exists(full)) return "";

        string text;
        try { text = await File.ReadAllTextAsync(full); }
        catch { return ""; }

        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var lineCount = normalized.Length == 0 ? 0 : lines.Length;
        var body = string.Join("\n", lines.Select(l => "+" + l));
        if (body.Length > 0 && !body.EndsWith("\n")) body += "\n";

        return $"diff --git a/{row.FullPath} b/{row.FullPath}\n" +
               $"new file mode 100644\n" +
               $"--- /dev/null\n" +
               $"+++ b/{row.FullPath}\n" +
               (lineCount > 0 ? $"@@ -0,0 +1,{lineCount} @@\n" : "") +
               body;
    }

    private void RunInIntegratedTerminal(string workingDirectory, string command)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(command)) return;

        _vm.ConsoleTabIndex = 3;
        var panel = _vm.SelectedProject?.TerminalPanel;
        panel?.RunCommand(command);
        _terminalHost?.FocusActive();
    }

    // ---------- package.json launcher ----------

    private void LaunchScriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var project = _vm.SelectedProject;
        if (project == null) return;

        var packages = project.DiscoverPackages();
        if (packages.Count == 0) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            HasDropShadow = true,
            Background = (System.Windows.Media.Brush)FindResource("BgSurface"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Border1"),
            Foreground = (System.Windows.Media.Brush)FindResource("FgPrimary"),
        };

        // Root project first — scripts go in without a section header.
        var root = packages.FirstOrDefault(p => p.IsRoot);
        if (root != null)
        {
            AppendPackageItems(menu, root);
        }

        // Nested packages get section headers (monorepo workspaces etc.).
        var nested = packages.Where(p => !p.IsRoot).ToList();
        if (nested.Count > 0)
        {
            if (root != null) menu.Items.Add(new Separator());
            var header = new MenuItem
            {
                Header = $"Workspaces ({nested.Count})",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDim"),
            };
            menu.Items.Add(header);
            foreach (var pkg in nested)
                AppendPackageItems(menu, pkg, asSubmenu: true);
        }

        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    /// Build MenuItems for a single package. When `asSubmenu` is true the
    /// package gets wrapped in a single parent item (workspace case). When
    /// false, script items are appended inline (root project case).
    private void AppendPackageItems(ContextMenu menu, PackageInfo pkg, bool asSubmenu = false)
    {
        var tree = PackageJsonService.BuildTree(pkg.Scripts);
        if (asSubmenu)
        {
            var parent = new MenuItem { Header = pkg.DisplayName };
            if (tree.Children.Count == 0)
            {
                parent.IsEnabled = false;
                parent.Header = $"{pkg.DisplayName} — no scripts";
            }
            else
            {
                foreach (var node in tree.Children)
                    parent.Items.Add(BuildMenuItem(node, pkg));
            }
            menu.Items.Add(parent);
        }
        else
        {
            if (tree.Children.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "No scripts in package.json",
                    IsEnabled = false,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgDim"),
                });
                return;
            }
            foreach (var node in tree.Children)
                menu.Items.Add(BuildMenuItem(node, pkg));
        }
    }

    /// Recursively materialize a ScriptNode. Leaves wire up a Click handler
    /// that spawns the package manager. Parents with children render as
    /// submenus — if a parent is also a leaf (e.g. `test` exists alongside
    /// `test:unit`) we prepend a "Run 'test'" leaf inside its submenu.
    private MenuItem BuildMenuItem(ScriptNode node, PackageInfo pkg)
    {
        var item = new MenuItem { Header = node.Segment };

        if (node.HasChildren)
        {
            if (node.IsLeaf)
            {
                // Both a group AND a runnable script — add a "Run '<name>'"
                // entry at the top of the submenu plus a separator.
                var self = new MenuItem
                {
                    Header = $"Run '{node.FullName}'",
                    ToolTip = node.Command,
                };
                var captured = node.FullName!;
                self.Click += (_, _) => RunInIntegratedTerminal(
                    pkg.DirPath,
                    PackageJsonService.BuildRunCommand(pkg.PackageManager, captured));
                item.Items.Add(self);
                item.Items.Add(new Separator());
            }
            foreach (var child in node.Children)
                item.Items.Add(BuildMenuItem(child, pkg));
        }
        else if (node.IsLeaf)
        {
            item.ToolTip = node.Command;
            var captured = node.FullName!;
            item.Click += (_, _) => RunInIntegratedTerminal(
                pkg.DirPath,
                PackageJsonService.BuildRunCommand(pkg.PackageManager, captured));
        }

        return item;
    }

    // ---------- Skills menu (Pi-only) ----------

    private void SkillsButton_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        conv.RefreshSkills();
        SkillsPopup.IsOpen = true;
    }

    private void SkillsRefresh_Click(object sender, RoutedEventArgs e)
        => _vm.SelectedProject?.SelectedConversation?.RefreshSkills();

    private void SkillsAdd_Click(object sender, RoutedEventArgs e)
    {
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        var dlg = new SkillNameDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.SkillName;
        var dir = SkillsService.CreateSkill(name);
        if (dir == null)
        {
            MessageBox.Show(this, $"Could not create skill \"{name}\" — name invalid or already exists.",
                "JustCode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        conv.RefreshSkills();
        // Open the new SKILL.md so the user can flesh it out immediately.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{System.IO.Path.Combine(dir, "SKILL.md")}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void SkillsDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SkillPick pick) return;
        var conv = _vm.SelectedProject?.SelectedConversation;
        if (conv == null) return;
        var confirm = MessageBox.Show(this,
            $"Delete skill \"{pick.Name}\"?\n\n{pick.Path}",
            "Delete skill", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (SkillsService.DeleteSkill(pick.Path, _vm.SelectedProject?.WorkingDirectory))
            conv.RefreshSkills();
        else
            MessageBox.Show(this, "Could not delete skill — it may be outside the known skill roots.",
                "JustCode", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void MaxIterUp_Click(object sender, RoutedEventArgs e)
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null) return;
        MaxIterBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        c.MaxIterations = Math.Min(999, c.MaxIterations + 1);
    }

    private void MaxIterDown_Click(object sender, RoutedEventArgs e)
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null) return;
        MaxIterBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        c.MaxIterations = Math.Max(1, c.MaxIterations - 1);
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var r = FindVisualChild<T>(child);
            if (r != null) return r;
        }
        return null;
    }

    // ---- Immersive dark mode (title bar) ----
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_CAPTION_COLOR = 35;       // Windows 11 22H2+
    private const int DWMWA_BORDER_COLOR = 34;        // Windows 11 22H2+

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void TryEnableImmersiveDarkMode()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int enable = 1;
            var rc = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enable, sizeof(int));
            if (rc != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enable, sizeof(int));

            // Paint the caption and border in the same #1e1e1e as the client
            // area so the whole chrome is flush-dark from frame 1. COLORREF is
            // 0x00BBGGRR — #1e1e1e maps to 0x001e1e1e. Ignored on older Windows.
            int dark = 0x001e1e1e;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref dark, sizeof(int));
        }
        catch { }
    }

    // ---- Startup flash fix: swap the HWND class background brush to dark ----
    // WPF's HwndSource creates its top-level window with the default system
    // class brush (COLOR_WINDOW → white on stock themes). That brush is what
    // paints once on mapping, before WPF composes its first frame — which is
    // the white flash the user sees on launch. Swapping the class brush to
    // a #1e1e1e solid brush makes the pre-composition paint blend into the
    // XAML background and removes the flash.
    private const int GCLP_HBRBACKGROUND = -10;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
    private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    private static IntPtr _darkBackgroundBrush = IntPtr.Zero;

    private void TryPaintInitialDarkBackground()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (_darkBackgroundBrush == IntPtr.Zero)
                _darkBackgroundBrush = CreateSolidBrush(0x001e1e1e);
            if (_darkBackgroundBrush == IntPtr.Zero) return;

            if (IntPtr.Size == 8)
                SetClassLongPtr64(hwnd, GCLP_HBRBACKGROUND, _darkBackgroundBrush);
            else
                SetClassLong32(hwnd, GCLP_HBRBACKGROUND, unchecked((uint)_darkBackgroundBrush.ToInt32()));

            // Force an immediate erase with the new brush so the flash is
            // painted dark rather than landing on a stale white buffer.
            InvalidateRect(hwnd, IntPtr.Zero, true);
        }
        catch { }
    }

    /// Open a popup and run a focus/selection action once WPF has had a
    /// chance to realise the popup's visual tree. Directly focusing the
    /// input right after setting IsOpen=true races the layout pass and
    /// silently fails, so every call site used to repeat the same
    /// Dispatcher.BeginInvoke dance — this helper centralises it.
    private void OpenPopupWithFocus(
        System.Windows.Controls.Primitives.Popup popup,
        Action onOpened,
        System.Windows.Threading.DispatcherPriority priority =
            System.Windows.Threading.DispatcherPriority.Input)
    {
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(onOpened, priority);
    }
}
