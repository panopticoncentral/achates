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

    public async Task<MobileSession?> LoadAsync(string agentName, string peerId, string sessionId, CancellationToken ct = default)
    {
        var path = GetPath(agentName, peerId, sessionId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MobileSession>(stream, JsonOptions, ct);
    }

    public async Task SaveAsync(string agentName, string peerId, MobileSession session, CancellationToken ct = default)
    {
        var path = GetPath(agentName, peerId, session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        session.Updated = DateTimeOffset.UtcNow;
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, ct);
    }

    public Task DeleteAsync(string agentName, string peerId, string sessionId, CancellationToken ct = default)
    {
        var path = GetPath(agentName, peerId, sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MobileSessionInfo>> ListAsync(string agentName, string peerId, CancellationToken ct = default)
    {
        var dir = GetDirectory(agentName, peerId);
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json");
        var results = new List<MobileSessionInfo>(files.Length);

        foreach (var file in files)
        {
            var session = await LoadAsync(agentName, peerId, Path.GetFileNameWithoutExtension(file), ct);
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

        return results.OrderByDescending(s => s.Updated).ToList();
    }

    public async Task UpdateMetadataAsync(string agentName, string peerId, string sessionId, string title, CancellationToken ct = default)
    {
        var session = await LoadAsync(agentName, peerId, sessionId, ct);
        if (session is null) return;
        session.Title = title;
        await SaveAsync(agentName, peerId, session, ct);
    }

    private string GetPath(string agentName, string peerId, string sessionId)
        => Path.Combine(basePath, "agents", agentName, "sessions", "mobile", peerId, $"{sessionId}.json");

    private string GetDirectory(string agentName, string peerId)
        => Path.Combine(basePath, "agents", agentName, "sessions", "mobile", peerId);
}
