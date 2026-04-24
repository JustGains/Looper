using System.Windows.Documents;

namespace JustCode.Services;

/// <summary>
/// Shared inline-cap policy for any FlowDocument paragraph that streams
/// unbounded text (console tabs, integrated terminal). Layout cost in
/// WPF text rendering scales roughly linearly with inline count, so long
/// sessions cause keystroke latency to creep up without a cap. Dropping
/// the oldest half in a single batch when we cross the threshold keeps
/// the tail the user cares about while flattening the O(N²) cost of
/// trimming one run at a time.
/// </summary>
public static class FlowDocumentInlineLimiter
{
    public const int DefaultMaxInlines = 8000;
    public const int DefaultTrimToInlines = 4000;

    public static void Apply(InlineCollection inlines,
        int max = DefaultMaxInlines, int trimTo = DefaultTrimToInlines)
    {
        if (inlines.Count <= max) return;
        int toRemove = inlines.Count - trimTo;
        var first = inlines.FirstInline;
        while (toRemove-- > 0 && first != null)
        {
            var next = first.NextInline;
            inlines.Remove(first);
            first = next;
        }
    }
}
