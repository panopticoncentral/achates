using System.Text.Json;
using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

public sealed class MobileSessionStore(string basePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
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
        string agentName, DateTimeOffset? before = null, int limit = 50, CancellationToken ct = default)
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
            results.Add(new MobileSessionInfo(
                session.Id,
                session.Title,
                session.Created,
                session.Updated,
                session.Messages.Count,
                lastUserMessage?.Text));
        }

        IEnumerable<MobileSessionInfo> query = results.OrderByDescending(s => s.Updated);

        if (before.HasValue)
            query = query.Where(s => s.Updated < before.Value);

        var filtered = query.Take(limit + 1).ToList();
        var hasMore = filtered.Count > limit;
        if (hasMore) filtered.RemoveAt(filtered.Count - 1);

        return (filtered, hasMore);
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
