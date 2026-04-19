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

    private static readonly Regex ToolHeaderLine = new(@"^▸\s.+?\(", RegexOptions.Compiled);
    private static readonly Regex ToolResultLine = new(@"^⎿\s", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Directory.GetCurrentDirectory());
        DataContext = _vm;

        RestoreWindowBounds();

        _vm.ConsoleAppend += (_, chunk) =>
        {
            AppendStyled(chunk);
            if (AutoScrollBox.IsChecked == true)
                ConsoleBox.ScrollToEnd();
        };

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.WorkingDirectoryChanged += (_, _) => ConsolePara.Inlines.Clear();

        TasksTabs.SelectionChanged += (_, _) => QueueScrollTasks();

        ConsoleBox.SizeChanged += (_, _) => ApplyWordWrap();
        Loaded += (_, _) => ApplyWordWrap();

        Closing += (_, _) => _vm.SaveWindowBounds(Left, Top, Width, Height);
        Closed += (_, _) => _vm.Shutdown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableImmersiveDarkMode();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WordWrapConsole))
            ApplyWordWrap();
        else if (e.PropertyName == nameof(MainViewModel.TasksText)
              || e.PropertyName == nameof(MainViewModel.AutoScrollTasks))
            QueueScrollTasks();
    }

    private void QueueScrollTasks()
    {
        // Defer to Background priority so MarkdownViewer finishes re-rendering
        // before we measure/scroll.
        Dispatcher.BeginInvoke(new Action(MaybeScrollTasks),
            System.Windows.Threading.DispatcherPriority.Background);
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

    private void AppendStyled(string chunk)
    {
        foreach (var (text, rule) in Tokenize(ApplyCollapse(chunk)))
        {
            var run = new Run(text);
            if (rule?.ForegroundBrush is not null) run.Foreground = rule.ForegroundBrush;
            if (rule?.BackgroundBrush is not null) run.Background = rule.BackgroundBrush;
            if (rule?.WeightValue is { } w) run.FontWeight = w;
            if (rule?.StyleValue is { } fs) run.FontStyle = fs;
            if (rule?.Underline == true) run.TextDecorations = TextDecorations.Underline;
            ConsolePara.Inlines.Add(run);
        }
    }

    /// When CollapseToolCalls is on: shorten long tool-call headers and
    /// replace tool result bodies with a single-glyph placeholder.
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
        while (startIdx <= chunk.Length)
        {
            var nl = chunk.IndexOf('\n', startIdx);
            int segEnd = nl < 0 ? chunk.Length : nl + 1;
            var segment = chunk.Substring(startIdx, segEnd - startIdx);
            startIdx = segEnd;
            if (segment.Length == 0) break;

            var spans = new List<(int start, int len, StylingRule rule)>();
            foreach (var r in rules)
            {
                if (r.CompiledRegex is null) continue;
                foreach (Match m in r.CompiledRegex.Matches(segment))
                {
                    if (m.Length == 0) continue;
                    spans.Add((m.Index, m.Length, r));
                }
            }
            spans.Sort((a, b) => a.start != b.start ? a.start.CompareTo(b.start) : b.len.CompareTo(a.len));
            var accepted = new List<(int start, int len, StylingRule rule)>();
            int cursor = 0;
            foreach (var s in spans)
            {
                if (s.start < cursor) continue;
                accepted.Add(s);
                cursor = s.start + s.len;
            }

            int pos = 0;
            foreach (var a in accepted)
            {
                if (a.start > pos) yield return (segment.Substring(pos, a.start - pos), null);
                yield return (segment.Substring(a.start, a.len), a.rule);
                pos = a.start + a.len;
            }
            if (pos < segment.Length) yield return (segment.Substring(pos), null);
        }
    }

    private void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select working directory",
            InitialDirectory = _vm.WorkingDirectory,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.WorkingDirectoryInput = dlg.FolderName;
            _vm.CommitWorkingDirectoryNow();
        }
    }

    private void WorkDirBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkDirBox.SelectedItem is string path && !string.IsNullOrWhiteSpace(path))
        {
            _vm.WorkingDirectoryInput = path;
            _vm.CommitWorkingDirectoryNow();
        }
    }

    private void WorkDirBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _vm.CommitWorkingDirectoryNow();
            e.Handled = true;
        }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.ToggleStartStopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Looper error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e) => ConsolePara.Inlines.Clear();

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => _vm.OpenConfigInNotepad();

    private void MaybeScrollTasks()
    {
        if (!_vm.AutoScrollTasks) return;
        if (TasksTabs.SelectedIndex == 0)
        {
            TasksBox.ScrollToEnd();
            return;
        }
        // Markdown tab: MarkdownViewer wraps a FlowDocumentScrollViewer which
        // itself contains a ScrollViewer in its template.
        var fdsv = FindVisualChild<FlowDocumentScrollViewer>(TasksMarkdown);
        if (fdsv != null)
        {
            fdsv.ApplyTemplate();
            var sv = FindVisualChild<ScrollViewer>(fdsv);
            if (sv != null)
            {
                sv.ScrollToEnd();
                return;
            }
        }
        var direct = FindVisualChild<ScrollViewer>(TasksMarkdown);
        direct?.ScrollToEnd();
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var r = FindVisualChild<T>(child);
            if (r != null) return r;
        }
        return null;
    }

    private void MaxIterUp_Click(object sender, RoutedEventArgs e)
    {
        MaxIterBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.MaxIterations = Math.Min(999, _vm.MaxIterations + 1);
    }

    private void MaxIterDown_Click(object sender, RoutedEventArgs e)
    {
        MaxIterBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.MaxIterations = Math.Max(1, _vm.MaxIterations - 1);
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
