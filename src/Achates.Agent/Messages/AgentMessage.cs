using System.Text.Json.Serialization;

namespace Achates.Agent.Messages;

/// <summary>
/// Base type for all messages in an agent conversation.
/// Subclass this to add application-specific message types.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "tool_result")]
[JsonDerivedType(typeof(SummaryMessage), "summary")]
public abstract record AgentMessage
{
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
