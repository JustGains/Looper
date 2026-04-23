using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace JustCode;

public sealed class GitDiffViewer : Border
{
    private readonly TextEditor _editor;
    private readonly DiffHeatmap _heatmap;
    private readonly TextBlock _addedText;
    private readonly TextBlock _deletedText;
    private readonly TextBlock _hunksText;

    public GitDiffViewer()
    {
        Background = Brush("#0f0f10");
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(0);

        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas"),
            FontSize = 13,
            Background = Brush("#0f0f10"),
            Foreground = Brush("#d7d7db"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 12, 10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.ShowSpaces = false;
        _editor.Options.ShowTabs = false;
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.SelectionBrush = Brush("#0e639c");
        _editor.TextArea.TextView.LineTransformers.Add(new UnifiedDiffColorizer());

        _heatmap = new DiffHeatmap();
        _heatmap.LineSelected += line => _editor.ScrollToLine(line);

        _addedText = StatText("+0", "#b7f5c4");
        _deletedText = StatText("-0", "#ffb4b4");
        _hunksText = StatText("@@ 0", "#d8b4fe");

        var statsBar = new Border
        {
            Background = Brush("#17181b"),
            BorderBrush = Brush("#23252a"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            StatPill(_addedText, "#13271a", "#21422c"),
                            StatPill(_deletedText, "#35181b", "#5a262b"),
                            StatPill(_hunksText, "#251931", "#423057"),
                        }
                    },
                    new TextBlock
                    {
                        Text = "Clean diff view",
                        Foreground = Brush("#8f8f97"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right,
                    }
                }
            }
        };

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(_heatmap, 1);
        contentGrid.Children.Add(_editor);
        contentGrid.Children.Add(_heatmap);

        var root = new DockPanel();
        DockPanel.SetDock(statsBar, Dock.Top);
        root.Children.Add(statsBar);
        root.Children.Add(contentGrid);
        Child = root;
    }

    public void SetDiff(string text, string? fullPath = null)
    {
        var present = BuildPresentation(text ?? "");
        _editor.SyntaxHighlighting = TryGetHighlighting(fullPath);
        _editor.Document = new TextDocument(present.Text);
        _heatmap.SetLines(present.LineKinds);
        _addedText.Text = $"+{present.Added}";
        _deletedText.Text = $"-{present.Deleted}";
        _hunksText.Text = $"@@ {present.Hunks}";
        _editor.ScrollToHome();
    }

    private static DiffPresentation BuildPresentation(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var kinds = new List<DiffLineKind>(lines.Length);
        int added = 0, deleted = 0, hunks = 0;

        foreach (var line in lines)
        {
            if (ShouldHide(line)) continue;

            if (line.StartsWith("@@"))
            {
                hunks++;
                kept.Add(line);
                kinds.Add(DiffLineKind.Hunk);
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++ "))
            {
                added++;
                kept.Add(line);
                kinds.Add(DiffLineKind.Added);
            }
            else if (line.StartsWith('-') && !line.StartsWith("--- "))
            {
                deleted++;
                kept.Add(line);
                kinds.Add(DiffLineKind.Deleted);
            }
            else if (line.StartsWith("\\ No newline at end of file") || line.StartsWith("Binary files "))
            {
                kept.Add(line);
                kinds.Add(DiffLineKind.Note);
            }
            else
            {
                kept.Add(line);
                kinds.Add(DiffLineKind.Context);
            }
        }

        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[0]))
        {
            kept.RemoveAt(0);
            kinds.RemoveAt(0);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
            kinds.RemoveAt(kinds.Count - 1);
        }

        var text = kept.Count == 0 ? "No readable diff content." : string.Join("\n", kept);
        return new DiffPresentation(text, kinds, added, deleted, hunks);
    }

    private static bool ShouldHide(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        return line.StartsWith("diff --git ")
            || line.StartsWith("index ")
            || line.StartsWith("--- ")
            || line.StartsWith("+++ ")
            || line.StartsWith("new file mode ")
            || line.StartsWith("deleted file mode ")
            || line.StartsWith("old mode ")
            || line.StartsWith("new mode ")
            || line.StartsWith("similarity index ")
            || line.StartsWith("dissimilarity index ")
            || line.StartsWith("rename from ")
            || line.StartsWith("rename to ")
            || line.StartsWith("copy from ")
            || line.StartsWith("copy to ");
    }

    private static Border StatPill(TextBlock text, string bg, string border) => new()
    {
        Background = Brush(bg),
        BorderBrush = Brush(border),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(0, 0, 8, 0),
        Child = text,
    };

    private static TextBlock StatText(string text, string color) => new()
    {
        Text = text,
        Foreground = Brush(color),
        FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas"),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static IHighlightingDefinition? TryGetHighlighting(string? fullPath)
    {
        var ext = Path.GetExtension(fullPath ?? "");
        if (string.IsNullOrWhiteSpace(ext)) return null;
        return HighlightingManager.Instance.GetDefinitionByExtension(ext);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private sealed record DiffPresentation(string Text, IReadOnlyList<DiffLineKind> LineKinds, int Added, int Deleted, int Hunks);

    private enum DiffLineKind
    {
        Context,
        Added,
        Deleted,
        Hunk,
        Note,
    }

    private sealed class DiffHeatmap : FrameworkElement
    {
        private static readonly Brush Bg = Freeze("#151619");
        private static readonly Brush Context = Freeze("#2a2c31");
        private static readonly Brush Added = Freeze("#2da160");
        private static readonly Brush Deleted = Freeze("#c24c58");
        private static readonly Brush Hunk = Freeze("#9f67d8");
        private static readonly Brush Note = Freeze("#5a7b95");
        private static readonly Pen BorderPen = new(Freeze("#2a2d33"), 1);

        private IReadOnlyList<DiffLineKind> _kinds = Array.Empty<DiffLineKind>();
        public event Action<int>? LineSelected;

        public DiffHeatmap()
        {
            Width = 18;
            MinWidth = 18;
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;
        }

        public void SetLines(IReadOnlyList<DiffLineKind> kinds)
        {
            _kinds = kinds ?? Array.Empty<DiffLineKind>();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var rect = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Bg, BorderPen, rect);
            if (_kinds.Count == 0 || ActualHeight <= 0 || ActualWidth <= 0) return;

            double slot = ActualHeight / _kinds.Count;
            double w = Math.Max(4, ActualWidth - 6);
            double x = Math.Max(2, ActualWidth - w - 2);

            for (int i = 0; i < _kinds.Count; i++)
            {
                var y = i * slot;
                var h = Math.Max(1, slot);
                dc.DrawRectangle(BrushFor(_kinds[i]), null, new Rect(x, y, w, h));
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            CaptureMouse();
            JumpTo(e.GetPosition(this).Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
                JumpTo(e.GetPosition(this).Y);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void JumpTo(double y)
        {
            if (_kinds.Count == 0 || ActualHeight <= 0) return;
            var ratio = Math.Clamp(y / ActualHeight, 0, 0.999999);
            var line = 1 + (int)(ratio * _kinds.Count);
            LineSelected?.Invoke(line);
        }

        private static Brush BrushFor(DiffLineKind kind) => kind switch
        {
            DiffLineKind.Added => Added,
            DiffLineKind.Deleted => Deleted,
            DiffLineKind.Hunk => Hunk,
            DiffLineKind.Note => Note,
            _ => Context,
        };

        private static SolidColorBrush Freeze(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    private sealed class UnifiedDiffColorizer : DocumentColorizingTransformer
    {
        private static readonly SolidColorBrush ContextFg = Freeze("#d7d7db");
        private static readonly SolidColorBrush ContextDim = Freeze("#b8b8be");
        private static readonly SolidColorBrush AddFg = Freeze("#b7f5c4");
        private static readonly SolidColorBrush AddBg = Freeze("#112017");
        private static readonly SolidColorBrush DelFg = Freeze("#ffb4b4");
        private static readonly SolidColorBrush DelBg = Freeze("#2a1417");
        private static readonly SolidColorBrush HunkFg = Freeze("#dcc0ff");
        private static readonly SolidColorBrush HunkBg = Freeze("#21172a");
        private static readonly SolidColorBrush NoteFg = Freeze("#8fb7d4");

        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            if (text.Length == 0)
            {
                Paint(line, ContextDim, null, FontWeights.Normal);
                return;
            }

            if (text.StartsWith("@@"))
            {
                Paint(line, HunkFg, HunkBg, FontWeights.SemiBold);
                return;
            }

            if (text.StartsWith("\\ No newline at end of file") || text.StartsWith("Binary files "))
            {
                Paint(line, NoteFg, null, FontWeights.Normal);
                return;
            }

            if (text[0] == '+')
            {
                Paint(line, AddFg, AddBg, FontWeights.Normal);
                EmphasizePrefix(line, AddFg);
                return;
            }

            if (text[0] == '-')
            {
                Paint(line, DelFg, DelBg, FontWeights.Normal);
                EmphasizePrefix(line, DelFg);
                return;
            }

            Paint(line, ContextFg, null, FontWeights.Normal);
        }

        private void EmphasizePrefix(DocumentLine line, Brush foreground)
        {
            ChangeLinePart(line.Offset, Math.Min(line.Offset + 1, line.EndOffset), el =>
            {
                el.TextRunProperties.SetForegroundBrush(foreground);
                el.TextRunProperties.SetTypeface(new Typeface(
                    el.TextRunProperties.Typeface.FontFamily,
                    el.TextRunProperties.Typeface.Style,
                    FontWeights.Bold,
                    el.TextRunProperties.Typeface.Stretch));
            });
        }

        private void Paint(DocumentLine line, Brush foreground, Brush? background, FontWeight weight)
        {
            ChangeLinePart(line.Offset, line.EndOffset, el =>
            {
                if (background != null) el.TextRunProperties.SetBackgroundBrush(background);
                el.TextRunProperties.SetForegroundBrush(foreground);
                el.TextRunProperties.SetTypeface(new Typeface(
                    el.TextRunProperties.Typeface.FontFamily,
                    el.TextRunProperties.Typeface.Style,
                    weight,
                    el.TextRunProperties.Typeface.Stretch));
            });
        }

        private static SolidColorBrush Freeze(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
