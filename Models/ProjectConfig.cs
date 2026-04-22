namespace JustCode.Models;

public sealed class ProjectConfig
{
    public string? LastConversationId { get; set; }
    public List<string> ConversationOrder { get; set; } = new();
}
