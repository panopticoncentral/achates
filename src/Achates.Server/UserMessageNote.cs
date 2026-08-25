using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Server;

/// <summary>
/// Prepends an ephemeral note to a user message in an outgoing completion payload.
/// Shared by <see cref="TemporalContext"/> and <see cref="MemoryContext"/>; the
/// note is never persisted to <c>AgentRuntime.Messages</c>.
/// </summary>
internal static class UserMessageNote
{
    public static CompletionContext Prepend(
        CompletionContext context, int targetIndex, string note)
    {
        var messages = context.Messages.ToList();
        var target = messages[targetIndex];

        messages[targetIndex] = target switch
        {
            CompletionUserTextMessage text => text with { Text = $"{note}\n\n{text.Text}" },
            CompletionUserContentMessage content => content with
            {
                Content =
                [
                    new CompletionTextContent { Text = note },
                    .. content.Content,
                ],
            },
            _ => target, // Unknown user-message subtype; leave alone.
        };

        return context with { Messages = messages };
    }
}
