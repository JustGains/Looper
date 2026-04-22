namespace JustCode.Models;

public sealed class ConversationSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public CliTool Tool { get; set; } = CliTool.ClaudeCode;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxIterations { get; set; } = 5;
    public bool RalphEnabled { get; set; } = true;
    public bool KeepContext { get; set; } = false;
    public string? ClaudeModel { get; set; }
    public string? ClaudeEffort { get; set; }
    public string? CodexModel { get; set; }
    public string? CodexEffort { get; set; }

    /// Last captured CLI session id. Persisted so that a conversation can
    /// resume (Claude `--resume`, Codex `exec resume`) across app restarts.
    public string? LastSessionId { get; set; }

    /// UTC timestamp captured when LastSessionId was last updated. Used to
    /// auto-expire stale sessions (default 24h) so we don't try to resume
    /// into a context the model has long forgotten.
    public DateTime? LastSessionTimestamp { get; set; }

    /// Map of short `@label` (the portion after '@', e.g. "WorkoutScreen.tsx"
    /// or "workout/WorkoutScreen.tsx" for disambiguation) → full relative path.
    /// The TextBox stores `@label`; the loop runner submits the expanded form.
    public Dictionary<string, string> MentionMap { get; set; } = new();
}
