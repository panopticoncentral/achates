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

    /// <summary>For a chat-origin session, the initiator's session id.</summary>
    public string? OriginSessionId { get; set; }

    /// <summary>For a chat-origin session, the initiator agent's id.</summary>
    public string? PeerAgentId { get; set; }

    /// <summary>
    /// Per-session opt-in for spoken assistant replies. Default false; flipped
    /// by the <c>session.set_speech</c> RPC. When true AND the active agent
    /// has a voice (or a global default is configured), <see cref="Achates.Server.Speech.SpeechBroker"/>
    /// is wired into the streaming loop and emits <c>audio.block</c> events.
    /// </summary>
    public bool SpeechEnabled { get; set; }

    public List<AgentMessage> Messages { get; set; } = [];

    /// <summary>
    /// Build a session for the current turn's save: carry every persistable
    /// field forward from the existing on-disk record (if any), with a new
    /// message list. Centralizing this is a guardrail — every field added to
    /// <see cref="MobileSession"/> must be persisted across turn saves, and
    /// the previous "manually list each preserved field" pattern at the call
    /// sites silently dropped <see cref="SpeechEnabled"/> (and, on the resubmit
    /// path, the chat-origin pairing fields). New fields on this type are now
    /// auto-preserved here; tests pin the contract.
    /// </summary>
    public static MobileSession WithMessages(MobileSession? existing, string id, IEnumerable<AgentMessage> messages)
    {
        return new MobileSession
        {
            Id = id,
            Title = existing?.Title,
            Created = existing?.Created ?? DateTimeOffset.UtcNow,
            JobId = existing?.JobId,
            Source = existing?.Source,
            OriginSessionId = existing?.OriginSessionId,
            PeerAgentId = existing?.PeerAgentId,
            SpeechEnabled = existing?.SpeechEnabled ?? false,
            Messages = [.. messages],
        };
    }
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
    SessionSource? Source = null,
    bool SpeechEnabled = false,
    int Unread = 0);
