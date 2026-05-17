using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

public sealed class MobileSession
{
    public required string Id { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// If this session was created by a scheduled cron job run, the job's ID.
    /// Null for user-initiated sessions. Used by the session reaper to prune
    /// old cron run sessions.
    /// </summary>
    public string? JobId { get; set; }

    /// <summary>
    /// Origin of the session when it wasn't created by direct user interaction.
    /// Null for normal user (and cron — see <see cref="JobId"/>) sessions.
    /// </summary>
    public SessionSource? Source { get; set; }

    public List<AgentMessage> Messages { get; set; } = [];
}

/// <summary>
/// Distinguishes sessions that weren't started by the user directly. Only
/// chat-origin needs distinguishing today; null means a normal user/cron session.
/// </summary>
public enum SessionSource
{
    Chat,
}

public sealed record MobileSessionInfo(
    string Id,
    string? Title,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    int MessageCount,
    string? Preview,
    string? JobId,
    string? CronTaskName = null,
    SessionSource? Source = null);
