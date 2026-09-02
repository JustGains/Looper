using System.IO;
using System.Text.Json;
using JustCode.Models;

namespace JustCode.Services;

/// Per-project + per-conversation storage under `<workingDir>/.looper/`.
/// Layout:
///   .looper/project.json                     — project-level config
///   .looper/conversations/&lt;id&gt;/settings.json  — per-conv engine settings
///   .looper/conversations/&lt;id&gt;/prompt.txt     — per-conv prompt
///   .looper/conversations/&lt;id&gt;/tasks.md       — per-conv tasks
public static class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const string DeletedMarkerFileName = ".deleted";
    private const string DeletedConversationsDirName = "deleted-conversations";

    public static string LooperDir(string workingDirectory) =>
        Path.Combine(workingDirectory, ".looper");

    public static string ProjectConfigFile(string workingDirectory) =>
        Path.Combine(LooperDir(workingDirectory), "project.json");

    public static string ConversationsRoot(string workingDirectory) =>
        Path.Combine(LooperDir(workingDirectory), "conversations");

    private static string DeletedConversationsRoot(string workingDirectory) =>
        Path.Combine(LooperDir(workingDirectory), DeletedConversationsDirName);

    private static string DeletedConversationMarkerFile(string workingDirectory, string conversationId) =>
        Path.Combine(DeletedConversationsRoot(workingDirectory), Uri.EscapeDataString(conversationId) + ".deleted");

    public static string ConversationDir(string workingDirectory, string conversationId) =>
        Path.Combine(ConversationsRoot(workingDirectory), conversationId);

    public static string ConversationSettingsFile(string workingDirectory, string conversationId) =>
        Path.Combine(ConversationDir(workingDirectory, conversationId), "settings.json");

    public static string PromptFile(string workingDirectory, string conversationId) =>
        Path.Combine(ConversationDir(workingDirectory, conversationId), "prompt.txt");

    public static string TasksFile(string workingDirectory, string conversationId) =>
        Path.Combine(ConversationDir(workingDirectory, conversationId), "tasks.md");

    public static ProjectConfig LoadProject(string workingDirectory)
    {
        try
        {
            var path = ProjectConfigFile(workingDirectory);
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(path));
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new ProjectConfig();
    }

    public static void SaveProject(string workingDirectory, ProjectConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(LooperDir(workingDirectory));
            File.WriteAllText(ProjectConfigFile(workingDirectory),
                JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch { }
    }

    public static ConversationSettings LoadConversation(string workingDirectory, string id)
    {
        try
        {
            var path = ConversationSettingsFile(workingDirectory, id);
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<ConversationSettings>(File.ReadAllText(path));
                if (cfg != null)
                {
                    cfg.Id = id;
                    if (string.IsNullOrWhiteSpace(cfg.Name))
                        cfg.Name = id;
                    return cfg;
                }
            }
        }
        catch { }
        return new ConversationSettings { Id = id, Name = id };
    }

    public static void SaveConversation(string workingDirectory, ConversationSettings cfg)
    {
        try
        {
            Directory.CreateDirectory(ConversationDir(workingDirectory, cfg.Id));
            File.WriteAllText(ConversationSettingsFile(workingDirectory, cfg.Id),
                JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch { }
    }

    public static void DeleteConversation(string workingDirectory, string id)
    {
        try
        {
            MarkConversationDeleted(workingDirectory, id);

            var dir = ConversationDir(workingDirectory, id);
            if (!IsSafeConversationDir(workingDirectory, dir)) return;
            if (!Directory.Exists(dir)) return;

            try { File.WriteAllText(Path.Combine(dir, DeletedMarkerFileName), DateTime.UtcNow.ToString("O")); }
            catch { }

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    return;
                }
                catch
                {
                    if (i < 2) System.Threading.Thread.Sleep(75);
                }
            }
        }
        catch { }
    }

    private static void MarkConversationDeleted(string workingDirectory, string id)
    {
        try
        {
            Directory.CreateDirectory(DeletedConversationsRoot(workingDirectory));
            File.WriteAllText(DeletedConversationMarkerFile(workingDirectory, id), DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    private static bool IsConversationDeleted(string workingDirectory, string id)
    {
        try { return File.Exists(DeletedConversationMarkerFile(workingDirectory, id)); }
        catch { return false; }
    }

    public static List<string> EnumerateConversationIds(string workingDirectory)
    {
        try
        {
            var root = ConversationsRoot(workingDirectory);
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateDirectories(root)
                .Select(d => Path.GetFileName(d)!)
                .Where(n => !string.IsNullOrEmpty(n))
                .Where(n => !IsConversationDeleted(workingDirectory, n))
                .Where(n => !File.Exists(Path.Combine(ConversationDir(workingDirectory, n), DeletedMarkerFileName)))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// Returns a unique default name for a new conversation in this project.
    public static string SuggestName(string workingDirectory, IReadOnlyCollection<string> existingNames)
    {
        int i = 1;
        while (true)
        {
            var candidate = $"Conversation {i}";
            if (!existingNames.Any(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
            i++;
        }
    }

    public static string NewId()
    {
        // compact timestamp-based id: yyyyMMdd-HHmmss-xxx
        return $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 4)}";
    }

    private static bool IsSafeConversationDir(string workingDirectory, string dir)
    {
        try
        {
            var root = Path.GetFullPath(ConversationsRoot(workingDirectory))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && full.Length > root.Length;
        }
        catch { return false; }
    }

    /// If the project has legacy `.looper/prompt.txt` / `.looper/tasks.md`
    /// and no conversations yet, migrate them into a "Default" conversation.
    /// Returns the id of the migrated/seeded conversation, or null.
    public static string? MigrateLegacyIfNeeded(string workingDirectory, LoopSettings sharedDefaults)
    {
        try
        {
            if (EnumerateConversationIds(workingDirectory).Count > 0) return null;

            var legacyPrompt = Path.Combine(LooperDir(workingDirectory), "prompt.txt");
            var legacyTasks = Path.Combine(LooperDir(workingDirectory), "tasks.md");
            var legacySettings = Path.Combine(LooperDir(workingDirectory), "settings.json");

            var id = NewId();
            Directory.CreateDirectory(ConversationDir(workingDirectory, id));

            if (File.Exists(legacyPrompt))
                File.Move(legacyPrompt, PromptFile(workingDirectory, id), overwrite: true);
            if (File.Exists(legacyTasks))
                File.Move(legacyTasks, TasksFile(workingDirectory, id), overwrite: true);

            // Seed settings from shared defaults; clean up legacy per-dir settings.json
            var seeded = SeedFromDefaults(id, "Default", sharedDefaults);
            SaveConversation(workingDirectory, seeded);
            try { if (File.Exists(legacySettings)) File.Delete(legacySettings); } catch { }

            return id;
        }
        catch { return null; }
    }

    public static ConversationSettings SeedFromDefaults(string id, string name, LoopSettings defaults) => new()
    {
        Id = id,
        Name = name,
        Tool = defaults.Tool,
        TimeoutSeconds = defaults.TimeoutSeconds,
        MaxIterations = defaults.MaxIterations,
        RalphEnabled = defaults.RalphEnabled,
        KeepContext = defaults.KeepContext,
        ClaudeModel = defaults.ClaudeModel,
        ClaudeEffort = defaults.ClaudeEffort,
        CodexModel = defaults.CodexModel,
        CodexEffort = defaults.CodexEffort,
        IsTaskManagerEnabled = false,
    };
}
