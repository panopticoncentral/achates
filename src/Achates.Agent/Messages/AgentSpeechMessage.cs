namespace Achates.Agent.Messages;

/// <summary>
/// One utterance in an inter-agent conversation, attributed to a speaker.
/// Persisted into both the initiator's and the target's sessions for display
/// and dreamtime review. Excluded from rebuilt LLM context by
/// <see cref="MessageConversion"/> (the tool call/result already carry the
/// exchange for the initiator).
/// </summary>
public sealed record AgentSpeechMessage : AgentMessage
{
    public required string SpeakerAgentId { get; init; }
    public required string SpeakerDisplayName { get; init; }
    public required string ToAgentId { get; init; }
    public required string Text { get; init; }
}
