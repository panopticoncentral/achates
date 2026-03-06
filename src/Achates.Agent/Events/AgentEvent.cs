using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Events;

namespace Achates.Agent.Events;

/// <summary>
/// Base type for all agent lifecycle events.
/// </summary>
public abstract record AgentEvent;

/// <summary>
/// The agent loop has started.
/// </summary>
public sealed record AgentStartEvent : AgentEvent;

/// <summary>
/// The agent loop has completed. Contains all new messages produced during this run.
/// </summary>
public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> NewMessages) : AgentEvent;

/// <summary>
/// A new turn has started (one LLM response and its tool executions).
/// </summary>
public sealed record TurnStartEvent : AgentEvent;

/// <summary>
/// A turn has completed.
/// </summary>
public sealed record TurnEndEvent(
    AssistantMessage Response,
    IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

/// <summary>
/// A message has been added to the conversation (user, assistant, or tool result).
/// </summary>
public sealed record MessageStartEvent(AgentMessage Message) : AgentEvent;

/// <summary>
/// A streaming delta from the provider during assistant message generation.
/// </summary>
public sealed record MessageStreamEvent(CompletionEvent Inner) : AgentEvent;

/// <summary>
/// A message has been finalized.
/// </summary>
public sealed record MessageEndEvent(AgentMessage Message) : AgentEvent;

/// <summary>
/// A tool has started executing.
/// </summary>
public sealed record ToolStartEvent(
    string ToolCallId,
    string ToolName,
    Dictionary<string, object?> Arguments) : AgentEvent;

/// <summary>
/// A tool has reported progress during execution.
/// </summary>
public sealed record ToolProgressEvent(
    string ToolCallId,
    AgentToolResult Progress) : AgentEvent;

/// <summary>
/// A tool has finished executing.
/// </summary>
public sealed record ToolEndEvent(
    string ToolCallId,
    string ToolName,
    AgentToolResult Result,
    bool IsError) : AgentEvent;

/// <summary>
/// A tool was skipped because a steering message arrived.
/// </summary>
public sealed record ToolSkippedEvent(
    string ToolCallId,
    string ToolName,
    string Reason) : AgentEvent;
