namespace JustCode.Services;

/// Shared short-duration formatting. "mm:ss" for anything under an
/// hour, "h:mm:ss" past the hour mark. Returns "--:--" for a null
/// start — keeps the UI quiet when nothing is running yet.
public static class TimeFormat
{
    public const string Placeholder = "--:--";

    public static string Elapsed(DateTime? startUtc)
    {
        if (startUtc is null) return Placeholder;
        var t = DateTime.UtcNow - startUtc.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
