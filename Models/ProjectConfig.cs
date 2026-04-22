namespace JustCode.Models;

public sealed class ProjectConfig
{
    public string? LastConversationId { get; set; }
    public List<string> ConversationOrder { get; set; } = new();
    /// Which left-sidebar tab was active last time this project was open.
    /// "files" | "conversations" | "git". Null = default to conversations.
    public string? LastSidebarTab { get; set; }
}
