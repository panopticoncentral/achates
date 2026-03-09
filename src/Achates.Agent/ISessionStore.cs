using Achates.Agent.Messages;

namespace Achates.Agent;

/// <summary>
/// Persists agent conversation history by session key.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Load the message history for a session, or null if no session exists.
    /// </summary>
    Task<IReadOnlyList<AgentMessage>?> LoadAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save the current message history for a session.
    /// </summary>
    Task SaveAsync(string sessionKey, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a session and its message history.
    /// </summary>
    Task DeleteAsync(string sessionKey, CancellationToken cancellationToken = default);
}
