using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

/// <summary>
/// Per-session pending attributed messages, keyed by the chat tool-call id,
/// spliced into the session's saved message list right after the matching
/// <see cref="ToolResultMessage"/> at end-of-turn.
/// </summary>
public sealed class ChatTranscriptBuffer
{
    private readonly Dictionary<string, List<AgentSpeechMessage>> _byToolCall = [];

    public void Add(string toolCallId, AgentSpeechMessage message)
    {
        if (!_byToolCall.TryGetValue(toolCallId, out var list))
            _byToolCall[toolCallId] = list = [];
        list.Add(message);
    }

    public bool IsEmpty => _byToolCall.Count == 0;

    public IReadOnlyList<AgentMessage> Merge(IReadOnlyList<AgentMessage> messages)
    {
        if (IsEmpty) return messages;
        var result = new List<AgentMessage>(messages.Count + _byToolCall.Count * 2);
        foreach (var m in messages)
        {
            result.Add(m);
            if (m is ToolResultMessage tr && _byToolCall.TryGetValue(tr.ToolCallId, out var pending))
                result.AddRange(pending);
        }
        return result;
    }

    public void Clear() => _byToolCall.Clear();
}
