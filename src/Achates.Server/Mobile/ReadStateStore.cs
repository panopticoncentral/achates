using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Server.Mobile;

/// <summary>
/// Per-agent read-state. Persisted as
/// { "sessions": { "&lt;id&gt;": &lt;ms&gt; }, "last_read_timestamp": &lt;ms&gt; }.
/// A session's watermark is its explicit entry, falling back to the legacy
/// agent-wide <see cref="LastReadTimestamp"/> floor when it has no entry yet.
/// </summary>
public sealed class ReadState
{
    public Dictionary<string, long> Sessions { get; set; } = [];

    public long? LastReadTimestamp { get; set; }

    public long Watermark(string sessionId)
        => Sessions.TryGetValue(sessionId, out var ts) ? ts : (LastReadTimestamp ?? 0);
}

/// <summary>
/// Owns ~/.achates/agents/{agent}/read-state.json. Small atomic writes — never
/// touches the (large) session transcript files.
/// </summary>
public sealed class ReadStateStore(string basePath)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private SemaphoreSlim LockFor(string agentName)
        => _locks.GetOrAdd(agentName, _ => new SemaphoreSlim(1, 1));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private string PathFor(string agentName)
        => Path.Combine(basePath, "agents", agentName, "read-state.json");

    public async Task<ReadState> LoadAsync(string agentName)
    {
        var path = PathFor(agentName);
        if (!File.Exists(path)) return new ReadState();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ReadState>(stream, JsonOptions) ?? new ReadState();
        }
        catch
        {
            // Corrupt or unreadable — treat as never read (atomic temp+move writes
            // make this realistically only an external-interference case).
            return new ReadState();
        }
    }

    public async Task AdvanceSessionReadAsync(string agentName, string sessionId, long timestamp)
    {
        var gate = LockFor(agentName);
        await gate.WaitAsync();
        try
        {
            var state = await LoadAsync(agentName);
            if (state.Sessions.TryGetValue(sessionId, out var existing) && existing >= timestamp)
                return;
            state.Sessions[sessionId] = timestamp;
            await SaveAsync(agentName, state);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AdvanceLegacyAsync(string agentName, long timestamp)
    {
        var gate = LockFor(agentName);
        await gate.WaitAsync();
        try
        {
            var state = await LoadAsync(agentName);
            if (state.LastReadTimestamp is { } current && current >= timestamp)
                return;
            state.LastReadTimestamp = timestamp;
            await SaveAsync(agentName, state);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveSessionAsync(string agentName, string sessionId)
    {
        var path = PathFor(agentName);
        if (!File.Exists(path)) return;

        var gate = LockFor(agentName);
        await gate.WaitAsync();
        try
        {
            var state = await LoadAsync(agentName);
            if (state.Sessions.Remove(sessionId))
                await SaveAsync(agentName, state);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveAsync(string agentName, ReadState state)
    {
        var path = PathFor(agentName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
        File.Move(tempPath, path, overwrite: true);
    }
}
