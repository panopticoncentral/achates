using System.Collections.Concurrent;

namespace Achates.Server.Mobile;

/// <summary>
/// In-memory cache for per-agent preview state (last message, unread count).
/// Avoids scanning all session files on every agents.list call.
/// Thread-safe; entries are populated on first access and invalidated on mutations.
/// </summary>
public sealed class AgentStateCache
{
    private readonly ConcurrentDictionary<string, AgentPreviewState> _cache = new();

    public AgentPreviewState? Get(string agentName) =>
        _cache.TryGetValue(agentName, out var state) ? state : null;

    public void Set(string agentName, AgentPreviewState state) =>
        _cache[agentName] = state;

    public void Invalidate(string agentName) =>
        _cache.TryRemove(agentName, out _);

    public void InvalidateAll() =>
        _cache.Clear();

    /// <summary>
    /// Optimistically set unread count to zero without invalidating the rest of the preview.
    /// Retry loop handles concurrent updates from Set/ComputeAndCache.
    /// </summary>
    public void MarkRead(string agentName)
    {
        while (_cache.TryGetValue(agentName, out var state) && state.UnreadCount != 0)
        {
            if (_cache.TryUpdate(agentName, state with { UnreadCount = 0 }, state))
                break;
        }
    }
}

/// <summary>
/// Lightweight snapshot of an agent's preview state for the agent list.
/// </summary>
public sealed record AgentPreviewState(
    string? LastMessage,
    string? LastActivity,
    int UnreadCount);
