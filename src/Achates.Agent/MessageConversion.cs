using Achates.Agent.Messages;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Agent;

internal static class MessageConversion
{
    /// <summary>
    /// Default conversion from agent messages to provider messages.
    /// Skips any custom message types that don't map to the LLM contract.
    /// </summary>
    public static IReadOnlyList<CompletionMessage> DefaultConvertToLlm(
        IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<CompletionMessage>(messages.Count);

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    if (user.Content is { Count: > 0 })
                    {
                        var blocks = new List<CompletionUserContent>(user.Content.Count + 1)
                        {
                            new CompletionTextContent { Text = user.Text }
                        };
                        blocks.AddRange(user.Content);
                        result.Add(new CompletionUserContentMessage
                        {
                            Content = blocks,
                            Timestamp = user.Timestamp,
                        });
                    }
                    else
                    {
                        result.Add(new CompletionUserTextMessage
                        {
                            Text = user.Text,
                            Timestamp = user.Timestamp,
                        });
                    }
                    break;

                case AssistantMessage assistant:
                    result.Add(new CompletionAssistantMessage
                    {
                        Content = assistant.Content,
                        Model = assistant.Model,
                        CompletionUsage = assistant.Usage,
                        CompletionStopReason = assistant.StopReason,
                        ErrorMessage = assistant.Error,
                        Timestamp = assistant.Timestamp,
                    });
                    break;

                case ToolResultMessage toolResult:
                    result.Add(new CompletionToolResultMessage
                    {
                        ToolCallId = toolResult.ToolCallId,
                        ToolName = toolResult.ToolName,
                        Content = toolResult.Content,
                        IsError = toolResult.IsError,
                        Details = toolResult.Details,
                        Timestamp = toolResult.Timestamp,
                    });
                    break;

                case SummaryMessage summary:
                    result.Add(new CompletionUserTextMessage
                    {
                        Text = $"[Summary of earlier conversation]\n{summary.Summary}",
                        Timestamp = summary.Timestamp,
                    });
                    break;

                // Custom message types are silently skipped — they exist
                // for the application layer, not the LLM.
            }
        }

        return result;
    }
}
