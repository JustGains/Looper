using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JustCode.Models;

namespace JustCode.Services;

/// Generates short conversation names through OpenRouter. This is intentionally
/// independent of the selected conversation CLI so naming does not create or
/// mutate Claude/Codex/Pi sessions.
public static class ConversationNamer
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly Regex DefaultNamePattern =
        new(@"^Conversation\s+\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsDefaultName(string name, string id)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (string.Equals(name, id, StringComparison.Ordinal)) return true;
        if (string.Equals(name.Trim(), "Default", StringComparison.OrdinalIgnoreCase)) return true;
        return DefaultNamePattern.IsMatch(name.Trim());
    }

    public static async Task<string?> GenerateTitleAsync(
        string apiKey,
        string model,
        string userPrompt,
        CancellationToken ct = default)
    {
        var (title, _) = await TryGenerateTitleAsync(apiKey, model, userPrompt, ct).ConfigureAwait(false);
        return title;
    }

    public static async Task<(string? Title, string? Error)> TryGenerateTitleAsync(
        string apiKey,
        string model,
        string userPrompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (null, "no api key");
        if (string.IsNullOrWhiteSpace(userPrompt)) return (null, "empty prompt");

        var effectiveModel = string.IsNullOrWhiteSpace(model)
            ? LoopSettings.DefaultOpenRouterTitleModel
            : model.Trim();

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/justgains/looper");
        req.Headers.TryAddWithoutValidation("X-OpenRouter-Title", "Looper");

        var body = new
        {
            model = effectiveModel,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Write concise tab titles for coding-agent conversations. Reply with only the title text."
                },
                new
                {
                    role = "user",
                    content =
                        "Write a concise 3-6 word title summarizing this user request. " +
                        "No quotes, no markdown, no trailing punctuation, no prefix.\n\n" +
                        "Request:\n" + Truncate(userPrompt, 1500)
                }
            },
            temperature = 0.2,
            max_tokens = 24,
            stream = false,
        };

        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var reason = $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}";
                LogDebug($"model={effectiveModel} {reason} body={Truncate(raw, 600)}");
                return (null, reason);
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
                var sanitized = Sanitize(content ?? "");
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    var reason = "empty title after sanitize";
                    LogDebug($"model={effectiveModel} {reason} content={Truncate(content, 200)}");
                    return (null, reason);
                }
                return (sanitized, null);
            }
            catch (Exception ex)
            {
                var reason = $"parse error: {ex.GetType().Name}";
                LogDebug($"model={effectiveModel} {reason} body={Truncate(raw, 600)} ex={ex.Message}");
                return (null, reason);
            }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, "cancelled");
        }
        catch (TaskCanceledException)
        {
            var reason = "timeout (30s)";
            LogDebug($"model={effectiveModel} {reason}");
            return (null, reason);
        }
        catch (Exception ex)
        {
            var reason = $"{ex.GetType().Name}: {ex.Message}";
            LogDebug($"model={effectiveModel} {reason}");
            return (null, reason);
        }
    }

    private static readonly object LogLock = new();
    private static string? _logPath;

    public static string LogFilePath
    {
        get
        {
            if (_logPath != null) return _logPath;
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appdata, ConfigStore.AppId, "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "conversation-namer.log");
            return _logPath;
        }
    }

    private static void LogDebug(string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(
                    LogFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }

    private static string? Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string? line = null;
        foreach (var rawLine in raw.Split('\n'))
        {
            var t = rawLine.Trim().TrimEnd('\r');
            if (t.Length == 0) continue;
            if (t.StartsWith("```")) continue;
            line = t;
            break;
        }
        if (line == null) return null;

        foreach (var prefix in new[] { "Title:", "title:", "TITLE:" })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                line = line.Substring(prefix.Length).TrimStart();
                break;
            }
        }

        for (int i = 0; i < 4; i++)
        {
            var before = line;
            line = line.Trim('"', '\'', '`', '*', ' ', '\t');
            if (line == before) break;
        }

        line = Regex.Replace(line, @"\s+", " ").TrimEnd('.', '!', '?', ',', ';', ':');
        if (line.Length == 0) return null;
        if (line.Length > 60) line = line.Substring(0, 60).TrimEnd();
        return line;
    }
}
