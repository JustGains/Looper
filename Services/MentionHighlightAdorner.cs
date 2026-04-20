using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Looper.Services;

/// Renders `@path` tokens in a TextBox as filled pills showing only the
/// filename. The TextBox still holds the full path as its actual Text value
/// — the adorner paints an opaque rectangle over the original characters
/// and draws `@filename.ext` on top. What's submitted to the CLI is the
/// full path.
public sealed class MentionHighlightAdorner : Adorner
{
    private static readonly Regex MentionRegex = new(
        @"@(?:""([^""\r\n]+)""|([^\s""]+))", RegexOptions.Compiled);

    // Pill colors — translucent fill so the TextBox's own text shows through.
    private static readonly Brush PillFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x1a, 0x4e, 0x74)));
    private static readonly Pen PillPen = Freeze(new Pen(
        new SolidColorBrush(Color.FromArgb(0xaa, 0x7d, 0xc4, 0xff)), 1));

    private readonly TextBox _tb;
    private ScrollViewer? _scrollViewer;

    public MentionHighlightAdorner(TextBox target) : base(target)
    {
        _tb = target;
        IsHitTestVisible = false;
        // Intentionally NOT subscribing to LayoutUpdated — it fires far too
        // often (caret blink, focus change, etc.) and caused a noticeable
        // typing lag. Text/size/scroll coverage is sufficient.
        _tb.TextChanged += (_, _) => InvalidateVisual();
        _tb.SizeChanged += (_, _) => InvalidateVisual();
        _tb.Loaded += (_, _) => TryHookScroll();
        TryHookScroll();
    }

    private void TryHookScroll()
    {
        if (_scrollViewer != null) return;
        _scrollViewer = FindScrollViewer(_tb);
        if (_scrollViewer != null)
            _scrollViewer.ScrollChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = _tb.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (text.IndexOf('@') < 0) return;

        foreach (Match m in MentionRegex.Matches(text))
        {
            int start = m.Index;
            int end = m.Index + m.Length - 1;
            if (start < 0 || end < start) continue;

            var startRect = SafeRect(start);
            var endRect = SafeRect(end);
            if (startRect is null || endRect is null) continue;

            if (Math.Abs(startRect.Value.Top - endRect.Value.Top) < 0.5)
            {
                DrawPill(dc, new Rect(
                    startRect.Value.X,
                    startRect.Value.Y,
                    endRect.Value.Right - startRect.Value.X,
                    startRect.Value.Height));
            }
            else
            {
                double y = startRect.Value.Top;
                int lineStart = start;
                for (int i = start; i <= end; i++)
                {
                    var r = SafeRect(i);
                    if (r is null) continue;
                    if (Math.Abs(r.Value.Top - y) > 0.5)
                    {
                        var lineRect = LineSpan(lineStart, i - 1);
                        if (lineRect.HasValue) DrawPill(dc, lineRect.Value);
                        y = r.Value.Top;
                        lineStart = i;
                    }
                }
                var tail = LineSpan(lineStart, end);
                if (tail.HasValue) DrawPill(dc, tail.Value);
            }
        }
    }

    private Rect? LineSpan(int startCh, int endCh)
    {
        var s = SafeRect(startCh);
        var e = SafeRect(endCh);
        if (s is null || e is null) return null;
        return new Rect(s.Value.X, s.Value.Y, e.Value.Right - s.Value.X, s.Value.Height);
    }

    private static void DrawPill(DrawingContext dc, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var pillRect = new Rect(rect.X - 1, rect.Y, rect.Width + 2, rect.Height);
        double radius = Math.Min(4, rect.Height / 2);
        dc.DrawRoundedRectangle(PillFill, PillPen, pillRect, radius, radius);
    }

    private Rect? SafeRect(int charIndex)
    {
        try
        {
            var r = _tb.GetRectFromCharacterIndex(charIndex, trailingEdge: false);
            var r2 = _tb.GetRectFromCharacterIndex(charIndex, trailingEdge: true);
            if (r.IsEmpty || r2.IsEmpty) return null;
            return new Rect(r.X, r.Y, Math.Max(1.0, r2.X - r.X), r.Height);
        }
        catch { return null; }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? o)
    {
        if (o == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var child = VisualTreeHelper.GetChild(o, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static T Freeze<T>(T b) where T : Freezable { if (b.CanFreeze) b.Freeze(); return b; }
}
