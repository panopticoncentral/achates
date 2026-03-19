namespace Achates.Server;

/// <summary>
/// A channel binds a named transport instance to a named agent.
/// This is the runtime representation of a channel config entry.
/// </summary>
public sealed record ChannelBinding
{
    /// <summary>
    /// The channel name from config (e.g. "paul/websocket").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The transport that handles message delivery.
    /// </summary>
    public required WebSocketTransport Transport { get; init; }

    /// <summary>
    /// The name of the agent this channel is bound to.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// The resolved agent definition.
    /// </summary>
    public required AgentDefinition Agent { get; init; }
}
