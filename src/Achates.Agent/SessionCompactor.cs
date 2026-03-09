using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Agent;

/// <summary>
/// Compacts conversation history when it approaches the model's context window.
/// Summarizes older messages via the LLM and replaces them with a <see cref="SummaryMessage"/>.
/// </summary>
internal static class SessionCompactor
{
    /// <summary>
    /// Threshold: compact when estimated tokens exceed this fraction of context window.
    /// </summary>
    private const double CompactThreshold = 0.80;

    /// <summary>
    /// Target: after compaction, aim for this fraction of context window.
    /// </summary>
    private const double CompactTarget = 0.50;

    /// <summary>
    /// Minimum messages to preserve (never summarize these).
    /// </summary>
    private const int MinPreservedMessages = 4;

    /// <summary>
    /// Rough characters-per-token estimate for new messages without actual token counts.
    /// </summary>
    private const double CharsPerToken = 4.0;

    private const string SummarizationPrompt = """
        Summarize the following conversation concisely. Preserve: key facts, decisions made,
        identifiers (names, IDs, URLs), and any context needed to continue the conversation
        naturally. Do not include pleasantries or filler.
        """;

    /// <summary>
    /// Check if compaction is needed and perform it if so.
    /// Returns true if compaction was performed.
    /// </summary>
    internal static async Task<bool> CompactIfNeededAsync(
        List<AgentMessage> messages,
        Model model,
        Func<Model, CompletionContext, CompletionOptions?, CancellationToken, CompletionEventStream> getCompletions,
        CancellationToken cancellationToken)
    {
        if (messages.Count <= MinPreservedMessages)
            return false;

        var contextWindow = model.ContextWindow;
        if (contextWindow <= 0)
            return false;

        var estimatedTokens = EstimateTokenCount(messages);
        var threshold = (int)(contextWindow * CompactThreshold);

        if (estimatedTokens < threshold)
            return false;

        // Determine how many messages to summarize (from the start)
        var target = (int)(contextWindow * CompactTarget);
        var splitIndex = FindSplitIndex(messages, estimatedTokens, target);

        if (splitIndex <= 0)
            return false;

        var toSummarize = messages.Take(splitIndex).ToList();
        var toKeep = messages.Skip(splitIndex).ToList();

        try
        {
            var summary = await SummarizeAsync(toSummarize, model, getCompletions, cancellationToken);

            var summaryMessage = new SummaryMessage
            {
                Summary = summary,
                Timestamp = toSummarize[^1].Timestamp,
            };

            messages.Clear();
            messages.Add(summaryMessage);
            messages.AddRange(toKeep);

            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fallback: truncate instead of summarize
            var truncationMessage = new SummaryMessage
            {
                Summary = "Earlier conversation was truncated due to length.",
                Timestamp = toSummarize[^1].Timestamp,
            };

            messages.Clear();
            messages.Add(truncationMessage);
            messages.AddRange(toKeep);

            return true;
        }
    }

    /// <summary>
    /// Estimate total token count for a message list.
    /// Uses the last assistant message's input token count as a baseline,
    /// plus character-based estimation for messages added after it.
    /// </summary>
    internal static int EstimateTokenCount(IReadOnlyList<AgentMessage> messages)
    {
        // Find the last assistant message with usage data
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage { Usage.Input: > 0 } assistant)
            {
                // Start from the known input token count (which covers all messages up to and
                // including this assistant turn) plus the output tokens for this message
                var knownTokens = assistant.Usage.Input + assistant.Usage.Output;

                // Add estimates for any messages after this one
                for (var j = i + 1; j < messages.Count; j++)
                {
                    knownTokens += EstimateMessageTokens(messages[j]);
                }

                return knownTokens;
            }
        }

        // No assistant messages yet — estimate everything from characters
        return messages.Sum(EstimateMessageTokens);
    }

    /// <summary>
    /// Estimate token count for a single message using character count.
    /// </summary>
    internal static int EstimateMessageTokens(AgentMessage message)
    {
        var chars = message switch
        {
            UserMessage user => user.Text.Length,
            AssistantMessage assistant => assistant.Content.Sum(ContentCharCount),
            ToolResultMessage tool => tool.Content.Sum(ContentCharCount),
            SummaryMessage summary => summary.Summary.Length,
            _ => 0,
        };

        return Math.Max(1, (int)(chars / CharsPerToken));
    }

    private static int ContentCharCount(CompletionContent content) =>
        content switch
        {
            CompletionTextContent text => text.Text.Length,
            CompletionThinkingContent thinking => thinking.Thinking.Length,
            CompletionToolCall tool => tool.Name.Length + 50, // name + serialized args estimate
            _ => 50, // images, audio, files — rough placeholder
        };

    private static int ContentCharCount(CompletionUserContent content) =>
        content switch
        {
            CompletionTextContent text => text.Text.Length,
            _ => 50,
        };

    /// <summary>
    /// Find the index to split messages at. Messages before this index will be summarized.
    /// Ensures we don't split in the middle of a tool call sequence and that we preserve
    /// at least <see cref="MinPreservedMessages"/>.
    /// </summary>
    internal static int FindSplitIndex(
        IReadOnlyList<AgentMessage> messages, int currentTokens, int targetTokens)
    {
        var tokensToRemove = currentTokens - targetTokens;
        if (tokensToRemove <= 0)
            return 0;

        var maxSplitIndex = messages.Count - MinPreservedMessages;
        if (maxSplitIndex <= 0)
            return 0;

        // Walk forward, accumulating tokens until we've found enough to remove
        var accumulated = 0;
        var splitIndex = 0;

        for (var i = 0; i < maxSplitIndex; i++)
        {
            accumulated += EstimateMessageTokens(messages[i]);
            splitIndex = i + 1;

            if (accumulated >= tokensToRemove)
                break;
        }

        // Don't split in the middle of a tool call sequence.
        // If the message at splitIndex is a ToolResultMessage, advance past all
        // consecutive tool results to keep the tool call/result pair together.
        while (splitIndex < messages.Count && messages[splitIndex] is ToolResultMessage)
        {
            splitIndex++;
        }

        // If advancing past tool results consumed too many messages, back up to before
        // the tool call sequence instead.
        if (splitIndex > messages.Count - MinPreservedMessages)
        {
            // Walk backwards from original position to find the start of the tool sequence
            var original = splitIndex;
            while (splitIndex > 0 && messages[splitIndex - 1] is ToolResultMessage or AssistantMessage { StopReason: CompletionStopReason.ToolUse })
            {
                splitIndex--;
            }

            // If we couldn't find a clean boundary, give up on compaction
            if (splitIndex <= 0)
                return 0;
        }

        return splitIndex;
    }

    private static async Task<string> SummarizeAsync(
        IReadOnlyList<AgentMessage> messages,
        Model model,
        Func<Model, CompletionContext, CompletionOptions?, CancellationToken, CompletionEventStream> getCompletions,
        CancellationToken cancellationToken)
    {
        // Build a text representation of the messages to summarize
        var conversationText = FormatMessagesForSummary(messages);

        var context = new CompletionContext
        {
            SystemPrompt = SummarizationPrompt,
            Messages =
            [
                new CompletionUserTextMessage { Text = conversationText },
            ],
        };

        var options = new CompletionOptions { MaxTokens = 2048 };
        var stream = getCompletions(model, context, options, cancellationToken);

        await foreach (var _ in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Consume the stream
        }

        var result = await stream.ResultAsync.ConfigureAwait(false);

        var text = result.Content
            .OfType<CompletionTextContent>()
            .Select(c => c.Text)
            .FirstOrDefault();

        return text ?? "Unable to generate summary.";
    }

    private static string FormatMessagesForSummary(IReadOnlyList<AgentMessage> messages)
    {
        var parts = new List<string>(messages.Count);

        foreach (var message in messages)
        {
            switch (message)
            {
                case SummaryMessage summary:
                    parts.Add($"[Previous summary]: {summary.Summary}");
                    break;

                case UserMessage user:
                    parts.Add($"User: {user.Text}");
                    break;

                case AssistantMessage assistant:
                    var textParts = assistant.Content
                        .OfType<CompletionTextContent>()
                        .Select(c => c.Text);
                    var toolCalls = assistant.Content
                        .OfType<CompletionToolCall>()
                        .Select(c => $"[Called tool: {c.Name}]");
                    var combined = string.Join("\n", textParts.Concat(toolCalls));
                    if (!string.IsNullOrEmpty(combined))
                        parts.Add($"Assistant: {combined}");
                    break;

                case ToolResultMessage tool:
                    var resultText = tool.Content
                        .OfType<CompletionTextContent>()
                        .Select(c => c.Text);
                    parts.Add($"[Tool result ({tool.ToolName})]: {string.Join("\n", resultText)}");
                    break;
            }
        }

        return string.Join("\n\n", parts);
    }
}
