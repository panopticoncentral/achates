using System.Text;
using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Enables inter-agent communication. An agent can discover other agents and
/// start a ping-pong conversation with them. The target agent runs in an isolated
/// AgentRuntime with its own tools (minus chat, to prevent cascade).
/// </summary>
internal sealed class ChatTool(
    string selfAgentName,
    IReadOnlyDictionary<string, AgentInfo> agents,
    IReadOnlyList<string>? allowList) : AgentTool
{
    private const int DefaultMaxTurns = 5;
    private const string DoneSignal = "<<DONE>>";

    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["agents", "chat"],
                "Action to perform. 'agents' lists available agents. 'chat' starts a conversation with another agent."),
            ["agent"] = StringSchema("Name of the agent to chat with. Required for 'chat'."),
            ["message"] = StringSchema("Initial message to send to the agent. Required for 'chat'."),
            ["max_turns"] = NumberSchema(
                $"Maximum number of back-and-forth exchanges (1-{DefaultMaxTurns}). Default: {DefaultMaxTurns}."),
        },
        required: ["action"]);

    public override string Name => "chat";
    public override string Description =>
        "Talk to another agent. Use 'agents' to see who's available, then 'chat' to start a conversation.";
    public override string Label => "Agent Chat";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "agents";

        return action switch
        {
            "agents" => ListAgents(),
            "chat" => await ChatAsync(arguments, onProgress, cancellationToken),
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

    private async Task<AgentToolResult> ChatAsync(
        Dictionary<string, object?> arguments,
        Func<AgentToolResult, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        var targetName = GetString(arguments, "agent");
        if (string.IsNullOrWhiteSpace(targetName))
            return TextResult("Error: 'agent' is required.");

        if (targetName.Equals(selfAgentName, StringComparison.OrdinalIgnoreCase))
            return TextResult("Error: you cannot chat with yourself.");

        if (!agents.TryGetValue(targetName, out var targetInfo))
            return TextResult($"Error: agent '{targetName}' not found. Use action 'agents' to see available agents.");

        if (!IsAllowed(targetName))
            return TextResult($"Error: you are not allowed to chat with agent '{targetName}'.");

        var message = GetString(arguments, "message");
        if (string.IsNullOrWhiteSpace(message))
            return TextResult("Error: 'message' is required.");

        var maxTurns = GetInt(arguments, "max_turns") ?? DefaultMaxTurns;
        maxTurns = Math.Clamp(maxTurns, 1, DefaultMaxTurns);

        // Build tools for both agents (excluding chat to prevent cascade)
        var selfInfo = agents[selfAgentName];
        var selfTools = BuildTargetTools(selfInfo.AgentDef);
        var targetTools = BuildTargetTools(targetInfo.AgentDef);

        var chatPreamble =
            $"\n\n## Inter-Agent Chat\n"
            + $"You are in a conversation with another agent. "
            + $"When the conversation is complete and you have nothing more to add, "
            + $"end your response with {DoneSignal} on its own line. "
            + $"Do not use {DoneSignal} if you still have questions or need more information.";

        // Create isolated runtimes for both agents
        var selfRuntime = new AgentRuntime(new AgentOptions
        {
            Model = selfInfo.AgentDef.Model,
            SystemPrompt = selfInfo.AgentDef.SystemPrompt + chatPreamble
                + $"\nYou are chatting with agent '{targetName}' ({targetInfo.Description ?? "no description"}).",
            Tools = selfTools,
            CompletionOptions = selfInfo.AgentDef.CompletionOptions,
        });

        var targetRuntime = new AgentRuntime(new AgentOptions
        {
            Model = targetInfo.AgentDef.Model,
            SystemPrompt = targetInfo.AgentDef.SystemPrompt + chatPreamble
                + $"\nYou are being consulted by agent '{selfAgentName}' ({selfInfo.Description ?? "no description"}).",
            Tools = targetTools,
            CompletionOptions = targetInfo.AgentDef.CompletionOptions,
        });

        var transcript = new StringBuilder();
        transcript.AppendLine($"**{selfAgentName}**: {message}");

        var currentMessage = message;
        var currentSpeaker = selfAgentName;
        var respondingRuntime = targetRuntime;
        var respondingName = targetName;
        var respondingDef = targetInfo.AgentDef;

        for (var turn = 0; turn < maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The responding agent gets the message
            var response = await RunAgentTurnAsync(
                respondingRuntime,
                $"[From {currentSpeaker}]: {currentMessage}",
                respondingDef.CostLedger,
                respondingName,
                cancellationToken);

            var cleanResponse = response.Replace(DoneSignal, "").Trim();
            transcript.AppendLine($"**{respondingName}** (turn {turn + 1}): {cleanResponse}");

            // Report progress
            if (onProgress is not null)
            {
                await onProgress(TextResult(
                    $"Turn {turn + 1}/{maxTurns}: {respondingName} responded."));
            }

            // Check if done
            if (response.Contains(DoneSignal, StringComparison.OrdinalIgnoreCase))
                break;

            // Last turn — no swap needed
            if (turn == maxTurns - 1)
                break;

            // Swap roles for ping-pong
            currentMessage = cleanResponse;
            currentSpeaker = respondingName;
            if (respondingRuntime == targetRuntime)
            {
                respondingRuntime = selfRuntime;
                respondingName = selfAgentName;
                respondingDef = selfInfo.AgentDef;
            }
            else
            {
                respondingRuntime = targetRuntime;
                respondingName = targetName;
                respondingDef = targetInfo.AgentDef;
            }
        }

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = transcript.ToString().Trim() }],
            Details = new { Agent = targetName, Turns = maxTurns },
        };
    }

    private static async Task<string> RunAgentTurnAsync(
        AgentRuntime runtime, string message, CostLedger? costLedger, string agentName,
        CancellationToken cancellationToken)
    {
        var stream = runtime.PromptAsync(message);
        var responseText = new StringBuilder();

        await foreach (var evt in stream.WithCancellation(cancellationToken))
        {
            switch (evt)
            {
                case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                    responseText.Append(delta.Delta);
                    break;

                case MessageEndEvent { Message: AssistantMessage assistantMsg }:
                    if (costLedger is not null)
                    {
                        _ = costLedger.AppendAsync(new CostEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Model = assistantMsg.Model,
                            Channel = "chat",
                            Peer = agentName,
                            InputTokens = assistantMsg.Usage.Input,
                            OutputTokens = assistantMsg.Usage.Output,
                            CacheReadTokens = assistantMsg.Usage.CacheRead,
                            CacheWriteTokens = assistantMsg.Usage.CacheWrite,
                            CostTotal = assistantMsg.Usage.Cost.Total,
                            CostInput = assistantMsg.Usage.Cost.Input,
                            CostOutput = assistantMsg.Usage.Cost.Output,
                            CostCacheRead = assistantMsg.Usage.Cost.CacheRead,
                            CostCacheWrite = assistantMsg.Usage.Cost.CacheWrite,
                        });
                    }
                    break;
            }
        }

        return responseText.ToString().Trim();
    }

    private static IReadOnlyList<AgentTool> BuildTargetTools(AgentDefinition agentDef)
    {
        var tools = new List<AgentTool>();

        // Add shared tools, excluding ChatTool to prevent cascade
        foreach (var tool in agentDef.Tools)
        {
            if (tool is not ChatTool)
                tools.Add(tool);
        }

        // Add per-agent tools
        var sharedMemoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
        tools.Add(new MemoryTool(sharedMemoryPath, agentDef.MemoryPath));
        if (agentDef.TodoPath is { } todoPath)
            tools.Add(new TodoTool(todoPath));
        if (agentDef.CostLedger is { } costLedger)
            tools.Add(new CostTool(costLedger));

        return tools;
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static int? GetInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetInt32();
        return null;
    }
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
