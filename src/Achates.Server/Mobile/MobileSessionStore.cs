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
        var path = GetPath(agentName, sessionId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MobileSession>(stream, JsonOptions, ct);
    }

    public async Task SaveAsync(string agentName, MobileSession session, CancellationToken ct = default)
    {
        var path = GetPath(agentName, session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        session.Updated = DateTimeOffset.UtcNow;
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, ct);
    }

    public Task DeleteAsync(string agentName, string sessionId, CancellationToken ct = default)
    {
        var path = GetPath(agentName, sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MobileSessionInfo>> ListAsync(string agentName, CancellationToken ct = default)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json");
        var results = new List<MobileSessionInfo>(files.Length);

        foreach (var file in files)
        {
            var session = await LoadAsync(agentName, Path.GetFileNameWithoutExtension(file), ct);
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

    public async Task UpdateMetadataAsync(string agentName, string sessionId, string title, CancellationToken ct = default)
    {
        var session = await LoadAsync(agentName, sessionId, ct);
        if (session is null) return;
        session.Title = title;
        await SaveAsync(agentName, session, ct);
    }

    /// <summary>
    /// Load sessions as a timeline, ordered oldest-first within the page, paginated from newest.
    /// </summary>
    public async Task<IReadOnlyList<MobileSession>> LoadTimelineAsync(
        string agentName, DateTimeOffset? before = null, int limit = 50, CancellationToken ct = default)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json");
        var sessions = new List<MobileSession>(files.Length);

        foreach (var file in files)
        {
            var session = await LoadAsync(agentName, Path.GetFileNameWithoutExtension(file), ct);
            if (session is not null)
                sessions.Add(session);
        }

        IEnumerable<MobileSession> query = sessions.OrderByDescending(s => s.Created);

        if (before.HasValue)
            query = query.Where(s => s.Created < before.Value);

        return query.Take(limit).Reverse().ToList();
    }

    /// <summary>
    /// Returns the most recent session by Created timestamp, or null if none exist.
    /// </summary>
    public async Task<MobileSession?> GetLatestSessionAsync(string agentName, CancellationToken ct = default)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return null;

        var files = Directory.GetFiles(dir, "*.json");
        MobileSession? latest = null;

        foreach (var file in files)
        {
            var session = await LoadAsync(agentName, Path.GetFileNameWithoutExtension(file), ct);
            if (session is not null && (latest is null || session.Created > latest.Created))
                latest = session;
        }

        return latest;
    }

    /// <summary>
    /// Merge the session chronologically before laterSessionId into it.
    /// Returns the merged session, or null if the later session or a predecessor wasn't found.
    /// </summary>
    public async Task<MobileSession?> MergeSessionsAsync(string agentName, string laterSessionId, CancellationToken ct = default)
    {
        var dir = GetDirectory(agentName);
        if (!Directory.Exists(dir)) return null;

        // Load all sessions to find the predecessor
        var files = Directory.GetFiles(dir, "*.json");
        var sessions = new List<MobileSession>(files.Length);

        foreach (var file in files)
        {
            var session = await LoadAsync(agentName, Path.GetFileNameWithoutExtension(file), ct);
            if (session is not null)
                sessions.Add(session);
        }

        sessions.Sort((a, b) => a.Created.CompareTo(b.Created));

        var laterIndex = sessions.FindIndex(s => s.Id == laterSessionId);
        if (laterIndex < 1) return null; // not found or no predecessor

        var earlier = sessions[laterIndex - 1];
        var later = sessions[laterIndex];

        // Prepend earlier messages and adopt its Created timestamp
        later.Messages.InsertRange(0, earlier.Messages);
        later.Created = earlier.Created;

        await DeleteAsync(agentName, earlier.Id, ct);
        await SaveAsync(agentName, later, ct);

        return later;
    }

    /// <summary>
    /// Split a session at a message boundary. Messages with Timestamp &lt;= afterTimestamp stay
    /// in the original; messages after go into a new session.
    /// </summary>
    public async Task<(MobileSession Original, MobileSession NewSegment)?> SplitSessionAsync(
        string agentName, string sessionId, long afterTimestamp, CancellationToken ct = default)
    {
        var session = await LoadAsync(agentName, sessionId, ct);
        if (session is null) return null;

        var splitIndex = -1;
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            if (session.Messages[i].Timestamp <= afterTimestamp)
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex < 0 || splitIndex >= session.Messages.Count - 1)
            return null; // nothing to split

        var keepMessages = session.Messages.GetRange(0, splitIndex + 1);
        var newMessages = session.Messages.GetRange(splitIndex + 1, session.Messages.Count - splitIndex - 1);

        session.Messages = keepMessages;

        var newSession = new MobileSession
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Created = DateTimeOffset.FromUnixTimeMilliseconds(newMessages[0].Timestamp),
            Messages = newMessages,
        };

        await SaveAsync(agentName, session, ct);
        await SaveAsync(agentName, newSession, ct);

        return (session, newSession);
    }

    private string GetPath(string agentName, string sessionId)
        => Path.Combine(basePath, "agents", agentName, "sessions", $"{sessionId}.json");

    private string GetDirectory(string agentName)
        => Path.Combine(basePath, "agents", agentName, "sessions");
}
