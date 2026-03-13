using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Messages;

namespace Achates.Server;

/// <summary>
/// Persists agent sessions as JSON files on disk.
/// Each session is stored at {basePath}/{channelId}/{peerId}.json.
/// </summary>
public sealed class FileSessionStore(string basePath) : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<AgentMessage>?> LoadAsync(
        string sessionKey, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionKey);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentMessage>>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveAsync(
        string sessionKey, IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, messages, JsonOptions, cancellationToken);
    }

    public Task DeleteAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionKey);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetPath(string sessionKey)
    {
        // Session key is "agentName/transportType:peerId"
        // Stored at {basePath}/agents/{agentName}/sessions/{transportType}/{peerId}.json
        var separatorIndex = sessionKey.IndexOf(':');
        if (separatorIndex < 0)
            return Path.Combine(basePath, $"{sessionKey}.json");

        var channelId = sessionKey[..separatorIndex];
        var peerId = sessionKey[(separatorIndex + 1)..];

        var slashIndex = channelId.IndexOf('/');
        if (slashIndex < 0)
            return Path.Combine(basePath, "agents", channelId, "sessions", $"{peerId}.json");

        var agentName = channelId[..slashIndex];
        var transportType = channelId[(slashIndex + 1)..];
        return Path.Combine(basePath, "agents", agentName, "sessions", transportType, $"{peerId}.json");
    }
}
