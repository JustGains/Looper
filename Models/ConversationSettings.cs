namespace Looper.Models;

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
}
