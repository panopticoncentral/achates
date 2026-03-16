using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

public sealed class MobileSession
{
    public required string Id { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentMessage> Messages { get; set; } = [];
}

public sealed record MobileSessionInfo(
    string Id,
    string? Title,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    int MessageCount,
    string? Preview);
