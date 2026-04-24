namespace JustCode.Services;

/// <summary>
/// Minimal dirty-flagged memoizer for cheap-to-compute-but-read-often
/// VM getters (queue header label, status captions, etc.). Each read
/// recomputes only when <see cref="Invalidate"/> has been called since
/// the last successful compute. Bindings that re-read a getter several
/// times per change (TextBlock.Text plus a Visibility trigger on the
/// same value) get a single compute per dirtying rather than per read.
/// </summary>
/// <remarks>
/// Intentionally a struct: no extra heap allocation, zero overhead beyond
/// the stored value and a bool flag. Not thread-safe — designed for the
/// single WPF dispatcher thread that owns the view-model.
/// </remarks>
public struct DirtyMemo<T>
{
    private T _value;
    private bool _dirty;

    public static DirtyMemo<T> Empty() => new() { _value = default!, _dirty = true };

    /// Flag the cache stale so the next `Read(compute)` call recomputes.
    public void Invalidate() => _dirty = true;

    /// Return the cached value, calling <paramref name="compute"/> only
    /// when the cache is dirty. Stores the fresh result and clears the
    /// dirty flag.
    public T Read(Func<T> compute)
    {
        if (_dirty)
        {
            _value = compute();
            _dirty = false;
        }
        return _value;
    }
}
