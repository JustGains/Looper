namespace JustCode.Services;

/// Common contract for per-CLI output formatters. One instance lives for the
/// duration of a single iteration, converts the CLI's structured stdout to
/// console chunks, and surfaces loop-control signals to `LoopRunner`.
public interface IIterationFormatter
{
    /// True while a thinking block is mid-stream. LoopRunner relaxes the
    /// inactivity timeout while this is true because long reasoning passes
    /// can go quiet.
    bool IsInThinking { get; }

    // Per-iteration signals (read by LoopRunner after the turn exits).
    bool IterationExitSignal { get; }
    string? IterationStatus { get; }
    bool IterationAskedQuestion { get; }
    int IterationToolErrors { get; }
    /// The provider rejected this whole turn and retrying the same prompt
    /// will produce the same rejection. LoopRunner short-circuits on this.
    bool IterationFatalError { get; }
    string? IterationFatalErrorMessage { get; }

    /// Convert one line of CLI stdout to a console chunk (may be empty).
    string Format(string line);

    event EventHandler<string>? SessionIdCaptured;
    event EventHandler<(long input, long output, long cached)>? TokenUsageReported;
    event EventHandler<string>? ToolCallInvoked;
    event EventHandler<int>? EstimatedOutputCharsAppended;
    event EventHandler<(string text, bool isThinking)>? NonToolBlockUpdated;
}
