using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Chat;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Thin façade over <see cref="ChatRoomManager"/>. An agent can discover other
/// agents (<c>agents</c>) and send a single message to one of them
/// (<c>ask</c>), receiving its reply. State (the continuing conversation with
/// the target) lives in the manager / target session, not here.
/// </summary>
internal sealed class ChatTool(
    string selfAgentName,
    IReadOnlyDictionary<string, AgentInfo> agents,
    IReadOnlyList<string>? allowList,
    ChatRoomManager? manager,
    string initiatorSessionId) : AgentTool
{
    private const string ActionAgents = "agents";
    private const string ActionAsk = "ask";

    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum([ActionAgents, ActionAsk],
                "Action to perform. 'agents' lists available agents. 'ask' sends one message to an agent and returns its reply."),
            ["agent"] = StringSchema("Name of the agent to message. Required for 'ask'."),
            ["message"] = StringSchema("Message to send to the agent. Required for 'ask'."),
        },
        required: ["action"]);

    public override string Name => "chat";
    public override string Label => "Agent Chat";
    public override string Description =>
        "Talk to another agent. 'agents' lists who's available; 'ask' sends one message to an agent and returns its reply. Call 'ask' again to continue — the other agent remembers this conversation.";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? ActionAgents;

        return action switch
        {
            ActionAgents => ListAgents(),
            ActionAsk => await AskAsync(toolCallId, arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private bool IsAllowed(string agentName) =>
        allowList is not { Count: > 0 } ||
        allowList.Any(a => a.Equals(agentName, StringComparison.OrdinalIgnoreCase));

    private AgentToolResult ListAgents()
    {
        var otherAgents = agents
            .Where(a => !a.Key.Equals(selfAgentName, StringComparison.OrdinalIgnoreCase)
                        && IsAllowed(a.Key))
            .ToList();

        if (otherAgents.Count == 0)
            return TextResult("No other agents are available.");

        var sb = new StringBuilder();
        sb.AppendLine("Available agents:");
        foreach (var (name, info) in otherAgents)
        {
            sb.AppendLine($"- **{name}**: {info.Description ?? "No description"}");
            if (info.ToolNames is { Count: > 0 })
                sb.AppendLine($"  Tools: {string.Join(", ", info.ToolNames)}");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> AskAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var targetName = GetString(arguments, "agent");
        if (string.IsNullOrWhiteSpace(targetName))
            return TextResult("Error: 'agent' is required.");

        if (targetName.Equals(selfAgentName, StringComparison.OrdinalIgnoreCase))
            return TextResult("Error: you cannot chat with yourself.");

        if (!agents.ContainsKey(targetName))
            return TextResult($"Error: agent '{targetName}' not found. Use action 'agents' to see available agents.");

        if (!IsAllowed(targetName))
            return TextResult($"Error: you are not allowed to chat with agent '{targetName}'.");

        var message = GetString(arguments, "message");
        if (string.IsNullOrWhiteSpace(message))
            return TextResult("Error: 'message' is required.");

        if (manager is null)
            return TextResult("Error: inter-agent chat is not available.");

        // Invariant: the transport binds the sink around every chat-tool turn (Task 6); unbound = a wiring bug.
        var sink = ChatSinkAccessor.Current
            ?? throw new InvalidOperationException("No chat sink bound for this turn.");

        var reply = await manager.AskAsync(
            selfAgentName, initiatorSessionId, targetName, message, toolCallId, sink, cancellationToken);

        return TextResult(reply);
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}

/// <summary>
/// Metadata about an agent for discovery by other agents.
/// </summary>
public sealed record AgentInfo
{
    public required AgentDefinition AgentDef { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? ToolNames { get; init; }

    /// <summary>
    /// Allowlist of agent names this agent can chat with.
    /// Null or empty means all other agents are allowed.
    /// </summary>
    public IReadOnlyList<string>? AllowChat { get; init; }
}

/// <summary>
/// Ambient per-turn binding of the <see cref="Achates.Server.Mobile.IChatSink"/>
/// the transport sets up before invoking the agent runtime, so <see cref="ChatTool"/>
/// can stream attributed inter-agent turns back to the initiator's live view.
/// </summary>
internal static class ChatSinkAccessor
{
    private static readonly AsyncLocal<Achates.Server.Mobile.IChatSink?> _current = new();

    public static Achates.Server.Mobile.IChatSink? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
