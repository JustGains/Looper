using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Looper.Services;
using Looper.ViewModels;
using Microsoft.Win32;

namespace Looper;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private ProjectViewModel? _subscribedProject;

    private static readonly Regex ToolHeaderLine = new(@"^▸\s.+?\(", RegexOptions.Compiled);
    private static readonly Regex ToolResultLine = new(@"^⎿\s", RegexOptions.Compiled);

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

    // ---------- commands ----------

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Add project — select working directory",
            InitialDirectory = _vm.SelectedProject?.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };
        if (dlg.ShowDialog(this) == true)
            _vm.AddProject(dlg.FolderName);
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
            MessageBox.Show(this, ex.ToString(), "Looper error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedProject?.SelectedConversation?.ConsoleParagraph.Inlines.Clear();
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
