using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using JustCode.Services;
using JustCode.ViewModels;
using Microsoft.Win32;

namespace JustCode;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private ProjectViewModel? _subscribedProject;

    private static readonly Regex ToolHeaderLine = new(@"^▸\s.+?\(", RegexOptions.Compiled);
    private static readonly Regex ToolResultLine = new(@"^⎿\s", RegexOptions.Compiled);

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

        RestoreWindowBounds();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ProjectAdded += (_, p) => HookProject(p);
        _vm.ProjectRemoved += (_, p) => UnhookProject(p);

        ConsoleBox.SizeChanged += (_, _) => ApplyWordWrap();

        Loaded += (_, _) =>
        {
            _vm.InitializeTabs(Directory.GetCurrentDirectory());
            foreach (var p in _vm.Projects) HookProject(p);
            SubscribeToSelectedProject();
            AttachSelectedConversationDocument();
            ApplyWordWrap();
            AttachMentionHighlightAdorner();
        };

        Closing += (_, _) => _vm.SaveWindowBounds(Left, Top, Width, Height);
        Closed += (_, _) => _vm.Shutdown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableImmersiveDarkMode();
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
        // Replay persisted console history once, on first hook, through the
        // styling pipeline so it looks like the rest of the stream.
        var history = c.PopConsoleHistory();
        if (!string.IsNullOrEmpty(history))
            OnConversationConsoleAppend(c, history);
    }

    private void UnhookConversation(ConversationViewModel c) =>
        c.ConsoleAppend -= OnConversationConsoleAppend;

    private void OnConversationConsoleAppend(object? sender, string chunk)
    {
        if (sender is not ConversationViewModel c) return;
        AppendStyled(c, chunk);
        if (ReferenceEquals(c, _vm.SelectedProject?.SelectedConversation)
            && _vm.AutoScrollConsole)
        {
            ConsoleBox.ScrollToEnd();
        }
    }

    // ---- selection changes ----

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedProject))
        {
            SubscribeToSelectedProject();
            AttachSelectedConversationDocument();
            ApplyWordWrap();
            QueueScrollTasks();
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
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectViewModel.SelectedConversation))
        {
            AttachSelectedConversationDocument();
            ApplyWordWrap();
            QueueScrollTasks();
            CloseMentionPopup();
            HideMentionTooltip();
        }
    }

    private void AttachSelectedConversationDocument()
    {
        var c = _vm.SelectedProject?.SelectedConversation;
        if (c == null)
        {
            ConsoleBox.Document = new FlowDocument();
            return;
        }
        if (!ReferenceEquals(ConsoleBox.Document, c.ConsoleDocument))
            ConsoleBox.Document = c.ConsoleDocument;
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
        if (ConsoleBox?.Document == null) return;
        if (_vm.WordWrapConsole)
        {
            var w = Math.Max(100, ConsoleBox.ViewportWidth - 8);
            ConsoleBox.Document.PageWidth = w;
            ConsoleBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            ConsoleBox.Document.PageWidth = 6000;
            ConsoleBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
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

    private void AppendStyled(ConversationViewModel c, string chunk)
    {
        foreach (var (text, rule) in Tokenize(ApplyCollapse(chunk)))
        {
            var run = new Run(text);
            if (rule?.ForegroundBrush is not null) run.Foreground = rule.ForegroundBrush;
            if (rule?.BackgroundBrush is not null) run.Background = rule.BackgroundBrush;
            if (rule?.WeightValue is { } w) run.FontWeight = w;
            if (rule?.StyleValue is { } fs) run.FontStyle = fs;
            if (rule?.Underline == true) run.TextDecorations = TextDecorations.Underline;
            c.ConsoleParagraph.Inlines.Add(run);
        }
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

            var trimmed = line.TrimEnd('\n');
            if (ToolResultLine.IsMatch(trimmed))
            {
                sb.Append("⎿ …");
                if (line.EndsWith('\n')) sb.Append('\n');
            }
            else if (ToolHeaderLine.IsMatch(trimmed))
            {
                var open = trimmed.IndexOf('(');
                if (open > 0)
                    sb.Append(trimmed.Substring(0, open + 1)).Append('…').Append(')');
                else
                    sb.Append(trimmed);
                if (line.EndsWith('\n')) sb.Append('\n');
            }
            else
            {
                sb.Append(line);
            }
        }
        return sb.ToString();
    }

    private IEnumerable<(string text, StylingRule? rule)> Tokenize(string chunk)
    {
        var rules = _vm.Settings.StylingRules;
        if (rules.Count == 0 || string.IsNullOrEmpty(chunk))
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

        var openPaths = new HashSet<string>(
            _vm.Projects.Select(p => p.WorkingDirectory),
            StringComparer.OrdinalIgnoreCase);
        var recents = _vm.RecentWorkingDirectories
            .Where(d => !openPaths.Contains(d))
            .Take(8)
            .ToList();

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HasDropShadow = true,
        };

        foreach (var path in recents)
        {
            var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(leaf)) leaf = path;
            var item = new MenuItem
            {
                Tag = path,
                Header = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        new TextBlock { Text = leaf, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = path, Opacity = 0.6, FontSize = 10 },
                    },
                },
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is string p) _vm.AddProject(p);
            };
            menu.Items.Add(item);
        }

        if (recents.Count > 0)
            menu.Items.Add(new Separator());

        var openNew = new MenuItem { Header = "Open new workspace…" };
        openNew.Click += (_, _) => OpenFolderPickerAndAdd();
        menu.Items.Add(openNew);

        menu.IsOpen = true;
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

    private void RemoveConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ConversationViewModel c)
        {
            var project = _vm.Projects.FirstOrDefault(p => p.Conversations.Contains(c));
            project?.RemoveConversation(c);
        }
        e.Handled = true;
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
        conv.ConsoleParagraph.Inlines.Clear();
        conv.ClearSession();
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        ChatInputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.SelectedProject?.SelectedConversation?.EnqueueChat();
    }

    private void ChatInputBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateMentionPopup(ChatInputBox);

    private void ChatInputBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateMentionPopup(ChatInputBox);
        AutoSelectMentionIfCaretInside(ChatInputBox);
    }

    private void ChatInputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_activeMentionBox == ChatInputBox && MentionPopup.IsOpen && !MentionList.IsKeyboardFocusWithin)
            CloseMentionPopup();
    }

    private void ChatInputBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => ShowMentionTooltipAt(ChatInputBox, e.GetPosition(ChatInputBox));

    private void ChatInputBox_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        HideMentionTooltip();

    private void ChatInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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

    private void RemoveQueued_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is QueuedChatMessage msg)
            _vm.SelectedProject?.SelectedConversation?.RemoveQueued(msg);
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => _vm.OpenConfigInNotepad();

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
        }
        catch { }
    }
}
