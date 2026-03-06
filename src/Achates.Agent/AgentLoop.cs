using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Agent;

/// <summary>
/// Internal engine that drives the agent conversation loop.
/// </summary>
internal static class AgentLoop
{
    internal record LoopConfig
    {
        public required Model Model { get; init; }
        public required Func<IReadOnlyList<AgentMessage>, IReadOnlyList<CompletionMessage>> ConvertToLlm { get; init; }
        public Func<CompletionContext, CompletionContext>? TransformContext { get; init; }
        public required Func<Model, CompletionContext, CompletionOptions?, CancellationToken, CompletionEventStream> GetCompletions { get; init; }
        public CompletionOptions? CompletionOptions { get; init; }
        public IReadOnlyList<AgentTool>? Tools { get; init; }
        public string? SystemPrompt { get; init; }
        public required Func<IReadOnlyList<UserMessage>> DequeueSteeringMessages { get; init; }
        public required Func<IReadOnlyList<UserMessage>> DequeueFollowUpMessages { get; init; }
    }

    internal static async Task RunAsync(
        IReadOnlyList<UserMessage> prompts,
        List<AgentMessage> messages,
        LoopConfig config,
        AgentEventStream stream,
        CancellationToken cancellationToken)
    {
        var newMessages = new List<AgentMessage>();

        try
        {
            stream.Push(new AgentStartEvent());

            // Add initial prompt messages
            foreach (var prompt in prompts)
            {
                messages.Add(prompt);
                newMessages.Add(prompt);
                stream.Push(new MessageStartEvent(prompt));
                stream.Push(new MessageEndEvent(prompt));
            }

            // Outer loop: processes follow-up messages
            var hasMore = true;
            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Inner loop: processes tool calls and steering
                await RunTurnAsync(messages, config, stream, newMessages, cancellationToken)
                    .ConfigureAwait(false);

                // Check for follow-up messages
                var followUps = config.DequeueFollowUpMessages();
                if (followUps.Count > 0)
                {
                    foreach (var followUp in followUps)
                    {
                        messages.Add(followUp);
                        newMessages.Add(followUp);
                        stream.Push(new MessageStartEvent(followUp));
                        stream.Push(new MessageEndEvent(followUp));
                    }
                }
                else
                {
                    hasMore = false;
                }
            }

            stream.Push(new AgentEndEvent(newMessages));
            stream.End(newMessages);
        }
        catch (OperationCanceledException)
        {
            stream.Push(new AgentEndEvent(newMessages));
            stream.End(newMessages);
        }
        catch (Exception ex)
        {
            stream.Fault(ex);
        }
    }

    internal static async Task ContinueAsync(
        List<AgentMessage> messages,
        LoopConfig config,
        AgentEventStream stream,
        CancellationToken cancellationToken)
    {
        var newMessages = new List<AgentMessage>();

        try
        {
            stream.Push(new AgentStartEvent());

            var hasMore = true;
            while (hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await RunTurnAsync(messages, config, stream, newMessages, cancellationToken)
                    .ConfigureAwait(false);

                var followUps = config.DequeueFollowUpMessages();
                if (followUps.Count > 0)
                {
                    foreach (var followUp in followUps)
                    {
                        messages.Add(followUp);
                        newMessages.Add(followUp);
                        stream.Push(new MessageStartEvent(followUp));
                        stream.Push(new MessageEndEvent(followUp));
                    }
                }
                else
                {
                    hasMore = false;
                }
            }

            stream.Push(new AgentEndEvent(newMessages));
            stream.End(newMessages);
        }
        catch (OperationCanceledException)
        {
            stream.Push(new AgentEndEvent(newMessages));
            stream.End(newMessages);
        }
        catch (Exception ex)
        {
            stream.Fault(ex);
        }
    }

    private static async Task RunTurnAsync(
        List<AgentMessage> messages,
        LoopConfig config,
        AgentEventStream agentStream,
        List<AgentMessage> newMessages,
        CancellationToken cancellationToken)
    {
        var continueLoop = true;
        while (continueLoop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            continueLoop = false;

            agentStream.Push(new TurnStartEvent());

            // Build context and stream assistant response
            var assistantMessage = await StreamAssistantResponseAsync(
                messages, config, agentStream, cancellationToken).ConfigureAwait(false);

            messages.Add(assistantMessage);
            newMessages.Add(assistantMessage);

            // Execute tool calls if any
            var toolResults = new List<ToolResultMessage>();

            if (assistantMessage.StopReason == CompletionStopReason.ToolUse)
            {
                var toolCalls = assistantMessage.Content.OfType<CompletionToolCall>().ToList();
                var toolsByName = (config.Tools ?? []).ToDictionary(t => t.Name);
                var interrupted = false;

                foreach (var toolCall in toolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Check for steering messages before executing each tool
                    if (interrupted)
                    {
                        var skipResult = CreateSkipResult(toolCall, "Skipped: user provided new instructions");
                        messages.Add(skipResult);
                        newMessages.Add(skipResult);
                        toolResults.Add(skipResult);
                        agentStream.Push(new ToolSkippedEvent(toolCall.Id, toolCall.Name,
                            "User provided new instructions"));
                        continue;
                    }

                    var result = await ExecuteToolAsync(
                        toolCall, toolsByName, agentStream, cancellationToken).ConfigureAwait(false);

                    messages.Add(result);
                    newMessages.Add(result);
                    toolResults.Add(result);

                    // Check for steering after each tool completes
                    var steering = config.DequeueSteeringMessages();
                    if (steering.Count > 0)
                    {
                        interrupted = true;

                        foreach (var steerMsg in steering)
                        {
                            messages.Add(steerMsg);
                            newMessages.Add(steerMsg);
                            agentStream.Push(new MessageStartEvent(steerMsg));
                            agentStream.Push(new MessageEndEvent(steerMsg));
                        }
                    }
                }
            }

            agentStream.Push(new TurnEndEvent(assistantMessage, toolResults));

            // Continue inner loop if we have pending tool results (from tool use)
            // or steering messages were injected
            if (assistantMessage.StopReason == CompletionStopReason.ToolUse)
            {
                continueLoop = true;
            }
        }
    }

    private static async Task<AssistantMessage> StreamAssistantResponseAsync(
        List<AgentMessage> messages,
        LoopConfig config,
        AgentEventStream agentStream,
        CancellationToken cancellationToken)
    {
        // Convert agent messages to provider messages
        var completionMessages = config.ConvertToLlm(messages);

        // Build tools list
        var completionTools = config.Tools?.Select(t => t.ToCompletionTool()).ToList();

        // Build context
        var context = new CompletionContext
        {
            SystemPrompt = config.SystemPrompt,
            Messages = completionMessages,
            Tools = completionTools,
        };

        // Apply context transform if configured
        if (config.TransformContext is { } transform)
        {
            context = transform(context);
        }

        // Start streaming
        var completionStream = config.GetCompletions(
            config.Model, context, config.CompletionOptions, cancellationToken);

        await foreach (var evt in completionStream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            agentStream.Push(new MessageStreamEvent(evt));
        }

        var completionResult = await completionStream.ResultAsync.ConfigureAwait(false);

        var assistantMessage = new AssistantMessage
        {
            Content = completionResult.Content,
            Model = completionResult.Model,
            Usage = completionResult.CompletionUsage,
            StopReason = completionResult.CompletionStopReason,
            Error = completionResult.ErrorMessage,
        };

        agentStream.Push(new MessageEndEvent(assistantMessage));

        return assistantMessage;
    }

    private static async Task<ToolResultMessage> ExecuteToolAsync(
        CompletionToolCall toolCall,
        Dictionary<string, AgentTool> toolsByName,
        AgentEventStream agentStream,
        CancellationToken cancellationToken)
    {
        agentStream.Push(new ToolStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments));

        try
        {
            if (!toolsByName.TryGetValue(toolCall.Name, out var tool))
            {
                var errorResult = new AgentToolResult
                {
                    Content = [new CompletionTextContent { Text = $"Unknown tool: {toolCall.Name}" }],
                };
                agentStream.Push(new ToolEndEvent(toolCall.Id, toolCall.Name, errorResult, IsError: true));
                return CreateToolResultMessage(toolCall, errorResult, isError: true);
            }

            var result = await tool.ExecuteAsync(
                toolCall.Id,
                toolCall.Arguments,
                cancellationToken,
                onProgress: progress =>
                {
                    agentStream.Push(new ToolProgressEvent(toolCall.Id, progress));
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

            agentStream.Push(new ToolEndEvent(toolCall.Id, toolCall.Name, result, IsError: false));
            return CreateToolResultMessage(toolCall, result, isError: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorResult = new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"Tool execution failed: {ex.Message}" }],
            };
            agentStream.Push(new ToolEndEvent(toolCall.Id, toolCall.Name, errorResult, IsError: true));
            return CreateToolResultMessage(toolCall, errorResult, isError: true);
        }
    }

    private static ToolResultMessage CreateToolResultMessage(
        CompletionToolCall toolCall, AgentToolResult result, bool isError) =>
        new()
        {
            ToolCallId = toolCall.Id,
            ToolName = toolCall.Name,
            Content = result.Content,
            IsError = isError,
            Details = result.Details,
        };

    private static ToolResultMessage CreateSkipResult(CompletionToolCall toolCall, string reason) =>
        new()
        {
            ToolCallId = toolCall.Id,
            ToolName = toolCall.Name,
            Content = [new CompletionTextContent { Text = reason }],
            IsError = true,
        };
}
