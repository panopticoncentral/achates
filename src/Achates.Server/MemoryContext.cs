using Achates.Providers.Completions;
using Achates.Providers.Completions.Messages;

namespace Achates.Server;

/// <summary>
/// Injects the agent's always-loaded memory tiers into the outgoing completion
/// payload — never persisted to <c>AgentRuntime.Messages</c>, never baked into
/// the system prompt.
///
/// <para>
/// The two tiers inject at different points because they have different
/// volatility. <b>Core</b> goes at the HEAD (first user message): it changes
/// roughly nightly at dreamtime, so on almost every turn the bytes are
/// identical and the block sits inside the prefix that Anthropic and OpenAI
/// cache — paid on the first turn or two rather than on every turn.
/// (Not literally once per session: on turn 1 the first and latest user
/// message are the same message, so the head also carries the working block
/// and the temporal note. Both vanish from message 0 on turn 2, so the turn-1
/// prefix doesn't match and core is billed uncached twice, not once. From
/// turn 2 on it is stable.) <b>Working</b> goes at the TAIL (latest user
/// message) alongside the temporal note: it is small, and the agent edits it
/// mid-conversation, so freshness is the point.
/// </para>
///
/// <para>
/// Both blocks are recomputed only when a new user turn begins, so within-turn
/// tool iterations keep a byte-stable prefix. An agent that writes to working
/// memory mid-turn sees it on the next user turn — acceptable, since it wrote
/// the content itself.
/// </para>
/// </summary>
public static class MemoryContext
{
    /// <summary>
    /// Builds a transform for <see cref="Agent.AgentOptions.TransformContext"/>.
    /// The returned delegate is stateful (it caches the rendered blocks) and is
    /// expected to be bound to a single <see cref="Agent.AgentRuntime"/>.
    /// </summary>
    /// <param name="corePath">Path to the agent's <c>memory.md</c>.</param>
    /// <param name="workingPath">Path to the agent's <c>working.md</c>. May be null when <paramref name="includeWorking"/> is false.</param>
    /// <param name="includeWorking">
    /// False on the agent-to-agent consult path: injecting "raise this when we
    /// next talk" into a one-round consult risks the agent raising a
    /// user-directed intention with another agent.
    /// </param>
    public static Func<CompletionContext, CompletionContext> CreateTransform(
        string corePath,
        string? workingPath,
        bool includeWorking)
    {
        long lastSeenUserTimestamp = -1;
        string coreBlock = "";
        string workingBlock = "";
        var primed = false;

        return context =>
        {
            var firstUserIdx = -1;
            var latestUserIdx = -1;
            CompletionUserMessage? latestUser = null;

            for (var i = 0; i < context.Messages.Count; i++)
            {
                if (context.Messages[i] is not CompletionUserMessage cum) continue;
                if (firstUserIdx < 0) firstUserIdx = i;
                latestUserIdx = i;
                latestUser = cum;
            }
            if (latestUser is null) return context;

            if (!primed || latestUser.Timestamp != lastSeenUserTimestamp)
            {
                coreBlock = FormatCore(ReadOrNull(corePath));
                workingBlock = includeWorking && workingPath is not null
                    ? FormatWorking(ReadOrNull(workingPath))
                    : "";
                lastSeenUserTimestamp = latestUser.Timestamp;
                primed = true;
            }

            if (coreBlock.Length == 0 && workingBlock.Length == 0) return context;

            var result = context;
            // Working first: on a single-message payload both indices are the
            // same, and prepending working before core leaves core ahead of it.
            if (workingBlock.Length > 0)
                result = UserMessageNote.Prepend(result, latestUserIdx, workingBlock);
            if (coreBlock.Length > 0)
                result = UserMessageNote.Prepend(result, firstUserIdx, coreBlock);
            return result;
        };
    }

    /// <summary>Renders the core-memory block, or an empty string when there is nothing to show.</summary>
    public static string FormatCore(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? ""
            : $"""
               ## Core Memory

               Your durable notes, already loaded. Do not call the memory tool to read this back.

               {content.Trim()}
               """;

    /// <summary>Renders the working-memory block, or an empty string when there is nothing to show.</summary>
    public static string FormatWorking(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? ""
            : $"""
               ## Working Memory

               Live threads you are tracking, already loaded. Raise one when the conversation
               makes it relevant — not all at once, and never as a checklist you recite up
               front. Once a thread has been surfaced or resolved, remove it with the memory
               tool (`scope: working`).

               {content.Trim()}
               """;

    /// <summary>
    /// Reads a memory file, treating any IO failure as "no memory" rather than
    /// failing the turn — a missing or locked file must never break a conversation.
    /// </summary>
    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
