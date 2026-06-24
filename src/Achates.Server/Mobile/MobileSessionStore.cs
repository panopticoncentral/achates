using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

public sealed class MobileSessionStore(string basePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<MobileSession?> LoadAsync(string agentName, string sessionId, CancellationToken ct = default)
    {
        var path = FindPath(agentName, sessionId);
        if (path is null) return null;
        return await LoadFromPathAsync(path, ct);
    }

    public async Task SaveAsync(string agentName, MobileSession session, CancellationToken ct = default)
    {
        var existingPath = FindPath(agentName, session.Id);
        var newPath = BuildPath(agentName, session.Id, session.Created, session.Title);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        session.Updated = DateTimeOffset.UtcNow;

        if (existingPath is not null && existingPath != newPath)
            File.Delete(existingPath);

        await using var stream = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, ct);
    }

    public Task DeleteAsync(string agentName, string sessionId, CancellationToken ct = default)
    {
        var path = FindPath(agentName, sessionId);
        if (path is not null) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<(IReadOnlyList<MobileSessionInfo> Sessions, bool HasMore)> ListAsync(
        string agentName, DateTimeOffset? before = null, int limit = 50,
        CancellationToken ct = default, Func<string, long>? watermarkFor = null)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return ([], false);

        var files = Directory.GetFiles(dir, "*.json");
        var results = new List<MobileSessionInfo>(files.Length);

        foreach (var file in files)
        {
            var session = await LoadFromPathAsync(file, ct);
            if (session is null) continue;

            var lastUserMessage = session.Messages.OfType<UserMessage>().LastOrDefault();
            var cronTaskName = session.Messages.FirstOrDefault() is UserMessage { Hidden: true } first
                ? Cron.CronSessionMarker.TryParseJobName(first.Text)
                : null;
            var unread = watermarkFor is null
                ? 0
                : UnreadCalculator.UnreadFor(session, cronTaskName, watermarkFor(session.Id));
            results.Add(new MobileSessionInfo(
                session.Id,
                session.Title,
                session.Created,
                session.Updated,
                session.Messages.Count,
                lastUserMessage?.Text,
                session.JobId,
                cronTaskName,
                session.Source,
                session.SpeechEnabled,
                unread));
        }

        IEnumerable<MobileSessionInfo> query = results.OrderByDescending(s => s.Updated);

        if (before.HasValue)
            query = query.Where(s => s.Updated < before.Value);

        // Take one extra to probe hasMore, guarding against overflow when a caller
        // passes limit: int.MaxValue to mean "everything" (int.MaxValue + 1 wraps to
        // int.MinValue, and Take(negative) returns an empty sequence).
        var probe = limit == int.MaxValue ? int.MaxValue : limit + 1;
        var filtered = query.Take(probe).ToList();
        var hasMore = filtered.Count > limit;
        if (hasMore) filtered.RemoveAt(filtered.Count - 1);

        return (filtered, hasMore);
    }

    public static string ChatSessionId(string originSessionId, string targetAgentId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(originSessionId + "|" + targetAgentId));
        return "chat-" + Convert.ToHexStringLower(bytes)[..12];
    }

    public async Task<MobileSession> LoadOrCreateChatSessionAsync(
        string targetAgentId, string originSessionId, string peerAgentId, CancellationToken ct = default)
    {
        var id = ChatSessionId(originSessionId, targetAgentId);
        var existing = await LoadAsync(targetAgentId, id, ct);
        if (existing is not null) return existing;

        var session = new MobileSession
        {
            Id = id,
            Title = $"Chat with {peerAgentId}",
            Source = SessionSource.Chat,
            OriginSessionId = originSessionId,
            PeerAgentId = peerAgentId,
        };
        await SaveAsync(targetAgentId, session, ct);
        return session;
    }

    public async Task<MobileSession> CreateAsync(string agentName, CancellationToken ct = default)
    {
        var session = new MobileSession
        {
            Id = Guid.NewGuid().ToString("N")[..12],
        };
        await SaveAsync(agentName, session, ct);
        return session;
    }

    public async Task DeleteAllAsync(string agentName, CancellationToken ct = default)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
            File.Delete(file);
    }

    public async Task UpdateMetadataAsync(string agentName, string sessionId, string title, CancellationToken ct = default)
    {
        var session = await LoadAsync(agentName, sessionId, ct);
        if (session is null) return;
        session.Title = title;
        await SaveAsync(agentName, session, ct);
    }

    /// <summary>
    /// Migrates legacy session files ({id}.json) to the new temporal format
    /// ({yyyyMMdd-HHmmss}_{id}[_{slug}].json). Safe to call multiple times.
    /// </summary>
    public async Task MigrateAsync(IEnumerable<string> agentNames, CancellationToken ct = default)
    {
        foreach (var agentName in agentNames)
        {
            var dir = GetDirectory(agentName);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // Skip files already in new format (contain underscores)
                if (name.Contains('_')) continue;

                var session = await LoadFromPathAsync(file, ct);
                if (session is null) continue;

                var newPath = BuildPath(agentName, session.Id, session.Created, session.Title);
                File.Move(file, newPath);
            }
        }
    }

    /// <summary>
    /// Extracts the session ID from a session filename (new or legacy format).
    /// New format: {yyyyMMdd-HHmmss}_{id}[_{slug}].json → id
    /// Legacy format: {id}.json → id
    /// </summary>
    public static string ExtractSessionId(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split('_', 3);
        return parts.Length >= 2 ? parts[1] : name;
    }

    private string? FindPath(string agentName, string sessionId)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return null;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            if (ExtractSessionId(file) == sessionId)
                return file;
        }

        return null;
    }

    private string BuildPath(string agentName, string sessionId, DateTimeOffset created, string? title)
    {
        var dir = GetDirectory(agentName);
        var datePrefix = created.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        var slug = !string.IsNullOrWhiteSpace(title) ? $"_{Slugify(title)}" : "";
        return Path.Combine(dir, $"{datePrefix}_{sessionId}{slug}.json");
    }

    private async Task<MobileSession?> LoadFromPathAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<MobileSession>(stream, JsonOptions, ct);
    }

    private string GetDirectory(string agentName)
        => Path.Combine(basePath, "agents", agentName, "sessions");

    private static string Slugify(string title)
    {
        var chars = title.ToLowerInvariant().Select(c =>
            char.IsLetterOrDigit(c) ? c :
            c == ' ' ? '-' : '\0');

        var slug = new string(chars.Where(c => c != '\0').ToArray());

        // Collapse consecutive hyphens
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        slug = slug.Trim('-');

        if (slug.Length > 50)
            slug = slug[..50].TrimEnd('-');

        return slug;
    }
}
