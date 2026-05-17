using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

/// <summary>
/// Bridge the <c>ChatRoomManager</c> uses to (a) stream attributed turns to the
/// initiator's live view and (b) buffer attributed copies for persistence into
/// the initiator's session at end-of-turn.
/// </summary>
public interface IChatSink
{
    Task EmitTurnStartAsync(string speakerAgentId, string speakerName, string toAgentId, CancellationToken ct);
    Task EmitTurnDeltaAsync(string delta, CancellationToken ct);
    Task EmitTurnEndAsync(string text, CancellationToken ct);

    /// <summary>Buffer one attributed message for the initiator session, anchored to the chat tool call.</summary>
    void BufferForInitiator(string toolCallId, AgentSpeechMessage message);
}
