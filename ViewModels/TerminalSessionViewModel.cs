using System.ComponentModel;
using System.Runtime.CompilerServices;
using JustCode.Services;

namespace JustCode.ViewModels;

/// <summary>
/// One embedded terminal session — shell process, pseudo-console, and the
/// metadata that backs a tab in the Terminal panel's tab strip.
/// </summary>
public sealed class TerminalSessionViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SessionExited;
    public event EventHandler<ReadOnlyMemory<byte>>? Output;

    private readonly ConPtyTerminal _pty = new();
    private readonly object _outputHistoryLock = new();
    private readonly Queue<byte[]> _outputHistory = new();
    private const int MaxOutputHistoryBytes = 2 * 1024 * 1024;
    private int _outputHistoryBytes;
    private string _title;
    private bool _isActive;
    private bool _isRenaming;
    private bool _isDropTarget;
    private bool _dropAfter;
    private bool _hasExited;

    /// <summary>Stable id, used as the session id in bridge messages.</summary>
    public string Id { get; }

    public ShellProfile Shell { get; }

    public string WorkingDirectory { get; }

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value ?? ""; OnChanged(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnChanged(); }
    }

    /// Toggled by the UI when the user double-clicks a tab to rename it.
    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming == value) return; _isRenaming = value; OnChanged(); }
    }

    /// Set true while a tab drag-drop reorder operation is hovering this tab.
    /// Drives a leading-edge accent bar in the tab template so the user can
    /// see exactly where the tab will land before they release.
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget == value) return; _isDropTarget = value; OnChanged(); }
    }

    /// When the drop indicator is on this tab, true means "drop after this
    /// tab" (cursor is past the tab's center), false means "drop before".
    /// Lets the user see whether dragging will land on the leading or
    /// trailing edge before releasing.
    public bool DropAfter
    {
        get => _dropAfter;
        set { if (_dropAfter == value) return; _dropAfter = value; OnChanged(); }
    }

    public bool HasExited => _hasExited;

    public TerminalSessionViewModel(string id, ShellProfile shell, string workingDirectory, string title)
    {
        Id = id;
        Shell = shell;
        WorkingDirectory = workingDirectory;
        _title = title;
        _pty.Output += OnPtyOutput;
        _pty.Exited += (_, _) =>
        {
            _hasExited = true;
            SessionExited?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Start(int cols, int rows)
    {
        _pty.Start(WorkingDirectory, Shell.Exe, Shell.Args, cols, rows);
    }

    public byte[] GetOutputHistorySnapshot()
    {
        lock (_outputHistoryLock)
        {
            if (_outputHistoryBytes == 0) return Array.Empty<byte>();
            var snapshot = new byte[_outputHistoryBytes];
            var offset = 0;
            foreach (var chunk in _outputHistory)
            {
                Buffer.BlockCopy(chunk, 0, snapshot, offset, chunk.Length);
                offset += chunk.Length;
            }
            return snapshot;
        }
    }

    public void RecordOutputHistory(ReadOnlyMemory<byte> bytes) => AppendOutputHistory(bytes);

    public void Write(ReadOnlySpan<byte> bytes) => _pty.Write(bytes);
    public void Resize(int cols, int rows) => _pty.Resize(cols, rows);

    public void Dispose() => _pty.Dispose();

    private void OnPtyOutput(object? sender, ReadOnlyMemory<byte> bytes)
    {
        AppendOutputHistory(bytes);
        Output?.Invoke(this, bytes);
    }

    private void AppendOutputHistory(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var chunk = bytes.ToArray();
        if (chunk.Length > MaxOutputHistoryBytes)
        {
            var trimmed = new byte[MaxOutputHistoryBytes];
            Buffer.BlockCopy(chunk, chunk.Length - MaxOutputHistoryBytes, trimmed, 0, trimmed.Length);
            chunk = trimmed;
        }

        lock (_outputHistoryLock)
        {
            _outputHistory.Enqueue(chunk);
            _outputHistoryBytes += chunk.Length;
            byte[]? lastEvicted = null;
            while (_outputHistoryBytes > MaxOutputHistoryBytes && _outputHistory.Count > 0)
            {
                lastEvicted = _outputHistory.Dequeue();
                _outputHistoryBytes -= lastEvicted.Length;
            }
            // If we evicted any chunks and the most recent eviction ended
            // mid-ANSI-escape (CSI/OSC/ESC-introducer that hadn't met its
            // terminator), the front of what's left starts with the tail
            // of that sequence. Replaying it as-is makes xterm render
            // garbage. Trim front bytes until the parser drops back into
            // ground state.
            if (lastEvicted != null)
            {
                var trailingState = AnsiTrailingState(lastEvicted);
                if (trailingState != AnsiState.Ground)
                    SkipAnsiContinuationFromFront(trailingState);
            }
        }
    }

    /// <summary>
    /// Tracks the parser state at the end of <paramref name="chunk"/>. We
    /// only care whether we're mid-sequence (and what kind), not full VT
    /// fidelity — the goal is to know how many bytes of the next chunk to
    /// drop after eviction so we never replay a half-escape.
    /// </summary>
    private static AnsiState AnsiTrailingState(ReadOnlySpan<byte> chunk)
    {
        var state = AnsiState.Ground;
        for (int i = 0; i < chunk.Length; i++)
            state = AnsiStep(state, chunk[i]);
        return state;
    }

    private void SkipAnsiContinuationFromFront(AnsiState state)
    {
        // Walk the front chunk byte-by-byte; once we hit Ground, slice the
        // remainder back into the queue. Pop and discard any chunk that's
        // entirely consumed by the in-progress sequence — bounded to keep
        // a runaway OSC from eating the whole history.
        const int MaxSkipChunks = 8;
        var skipped = 0;
        while (state != AnsiState.Ground && _outputHistory.Count > 0 && skipped < MaxSkipChunks)
        {
            var front = _outputHistory.Dequeue();
            _outputHistoryBytes -= front.Length;
            for (int i = 0; i < front.Length; i++)
            {
                state = AnsiStep(state, front[i]);
                if (state == AnsiState.Ground)
                {
                    var remainingLen = front.Length - (i + 1);
                    if (remainingLen > 0)
                    {
                        var remainder = new byte[remainingLen];
                        Buffer.BlockCopy(front, i + 1, remainder, 0, remainingLen);
                        // Re-queue at the front: ObservableCollection has no
                        // prepend, but a Queue's only insertion point is the
                        // tail — so we rebuild with remainder leading.
                        var rest = _outputHistory.ToArray();
                        _outputHistory.Clear();
                        _outputHistory.Enqueue(remainder);
                        _outputHistoryBytes += remainder.Length;
                        foreach (var c in rest)
                        {
                            _outputHistory.Enqueue(c);
                            _outputHistoryBytes += c.Length;
                        }
                    }
                    return;
                }
            }
            skipped++;
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="input"/> with ANSI escape sequences
    /// (CSI/OSC/single-char ESC) removed and carriage-return rewinds applied
    /// so progress-bar-style overwrites collapse to their final state. Each
    /// `\n` flushes a line buffer; a `\r` not followed by `\n` resets the
    /// cursor to column 0 so subsequent bytes overwrite earlier ones —
    /// matching what the terminal would actually render. `\b` rewinds one
    /// column. `\t` is preserved as a literal byte (no tab-stop expansion):
    /// downstream tools (`grep`, IDE viewers) interpret the literal tab and
    /// expanding here would force a tab-stop policy on the consumer.
    /// </summary>
    private const int LineBufferCap = 64 * 1024;

    public static byte[] StripAnsi(ReadOnlySpan<byte> input)
        => StripAnsi(input, out _);

    /// <summary>
    /// Same as <see cref="StripAnsi(ReadOnlySpan{byte})"/> but reports how
    /// many bytes were silently dropped because a single line exceeded
    /// <see cref="LineBufferCap"/>. Only `Save terminal output…` consumes
    /// this today: the runtime <see cref="AppendOutputHistory"/> path uses
    /// the same parser but doesn't apply the line-buffer cap (it caps the
    /// total queue size instead), so an extra runtime counter would track
    /// nothing the queue eviction doesn't already cover.
    /// </summary>
    public static byte[] StripAnsi(ReadOnlySpan<byte> input, out int lineOverflowBytes)
    {
        lineOverflowBytes = 0;
        if (input.Length == 0) return Array.Empty<byte>();
        // Output is bounded by input.Length: every code path either drops a
        // byte (ANSI/CR/BS) or writes one byte to the line buffer that will
        // eventually flush. Pre-sizing avoids the per-flush List<byte> doubling
        // that the prior implementation paid for every newline.
        var output = new byte[input.Length];
        var written = 0;
        var line = new byte[64];
        var lineLen = 0;
        var cursor = 0;
        var state = AnsiState.Ground;

        for (int i = 0; i < input.Length; i++)
        {
            var b = input[i];
            if (state != AnsiState.Ground)
            {
                state = AnsiStep(state, b);
                continue;
            }
            if (b == 0x1B) { state = AnsiState.Esc; continue; }
            if (b == (byte)'\n')
            {
                // Flush the line buffer plus the LF itself. CR-LF naturally
                // collapses: a leading CR reset cursor to 0 but the line
                // contents are still queued; LF flushes them as-is.
                Buffer.BlockCopy(line, 0, output, written, lineLen);
                written += lineLen;
                output[written++] = b;
                lineLen = 0;
                cursor = 0;
                continue;
            }
            if (b == (byte)'\r') { cursor = 0; continue; }
            if (b == 0x08) { if (cursor > 0) cursor--; continue; }

            if (cursor >= line.Length)
            {
                // Cursor only grows by one per write, so doubling is fine —
                // capped at 64 KiB to keep a runaway no-newline stream from
                // ballooning memory. Once at the cap we stop appending and
                // silently drop further bytes for this line; the rest of the
                // stream resumes normally on the next \n / \r boundary.
                if (line.Length >= LineBufferCap) { lineOverflowBytes++; continue; }
                var grow = Math.Min(line.Length * 2, LineBufferCap);
                Array.Resize(ref line, grow);
            }
            line[cursor] = b;
            if (cursor >= lineLen) lineLen = cursor + 1;
            cursor++;
        }
        // Trailing partial line (no final LF) still flushes.
        if (lineLen > 0)
        {
            Buffer.BlockCopy(line, 0, output, written, lineLen);
            written += lineLen;
        }
        if (written == output.Length) return output;
        var trimmed = new byte[written];
        Buffer.BlockCopy(output, 0, trimmed, 0, written);
        return trimmed;
    }

    private enum AnsiState
    {
        Ground,
        Esc,        // saw ESC, awaiting next byte
        Csi,        // inside CSI: ESC [ … final (0x40-0x7E)
        Osc,        // inside OSC: ESC ] … BEL or ESC \
        OscEsc,     // saw ESC inside OSC, awaiting \
    }

    private static AnsiState AnsiStep(AnsiState state, byte b)
    {
        switch (state)
        {
            case AnsiState.Ground:
                return b == 0x1B ? AnsiState.Esc : AnsiState.Ground;
            case AnsiState.Esc:
                if (b == (byte)'[') return AnsiState.Csi;
                if (b == (byte)']') return AnsiState.Osc;
                // Other ESC-prefixed sequences (single-char like ESC=, ESC>,
                // ESC 7, charset designators) complete on this byte.
                return AnsiState.Ground;
            case AnsiState.Csi:
                // CSI parameters are 0x30-0x3F, intermediates 0x20-0x2F,
                // final byte 0x40-0x7E ends the sequence.
                if (b >= 0x40 && b <= 0x7E) return AnsiState.Ground;
                return AnsiState.Csi;
            case AnsiState.Osc:
                if (b == 0x07) return AnsiState.Ground; // BEL terminator
                if (b == 0x1B) return AnsiState.OscEsc; // possible ST
                return AnsiState.Osc;
            case AnsiState.OscEsc:
                if (b == (byte)'\\') return AnsiState.Ground; // ESC \  (ST)
                // ESC followed by something else: bail out to Ground rather
                // than infinite-looping. Conservative — we'd rather drop a
                // few bytes than spin.
                return AnsiState.Ground;
            default:
                return AnsiState.Ground;
        }
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
