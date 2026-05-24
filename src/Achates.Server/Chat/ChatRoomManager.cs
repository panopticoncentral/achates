using System.Collections.Concurrent;
using System.Text;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Server.Mobile;

namespace Achates.Server.Chat;

/// <summary>
/// Stateless (except per-pairing locking) orchestrator for one inter-agent
/// consult round. Loads/creates the target's single continuing session,
/// reconstructs the target runtime's memory from prior attributed turns, runs
/// one streamed target reply, appends the two attributed messages, records the
/// target-side cost, and persists.
/// If the round is cancelled before the final save, the target session is
/// left unmodified on disk (the in-memory append is discarded).
/// </summary>
public sealed class ChatRoomManager(
    MobileSessionStore sessionStore,
    Func<string, AgentRuntimeFactory> runtimeFactoryFor)
{
    // Intentionally unpruned: one tiny SemaphoreSlim per (session,target) pair,
    // bounded by active sessions x agents; never disposed by design.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private SemaphoreSlim LockFor(string key)
        => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    public async Task<string> AskAsync(
        string initiatorAgentId, string initiatorSessionId, string targetAgentId,
        string message, string toolCallId, IChatSink sink, CancellationToken ct)
    {
        var gate = LockFor(initiatorSessionId + "|" + targetAgentId);
        await gate.WaitAsync(ct);
        try
        {
            var session = await sessionStore.LoadOrCreateChatSessionAsync(
                targetAgentId, initiatorSessionId, initiatorAgentId, ct);

            var outgoing = new AgentSpeechMessage
            {
                SpeakerAgentId = initiatorAgentId,
                SpeakerDisplayName = initiatorAgentId,
                ToAgentId = targetAgentId,
                Text = message,
            };
            await sink.EmitTurnStartAsync(initiatorAgentId, initiatorAgentId, targetAgentId, ct);
            await sink.EmitTurnEndAsync(message, ct);
            sink.BufferForInitiator(toolCallId, outgoing);

            // Reconstruct target memory from prior attributed turns (BEFORE adding outgoing).
            var seed = new List<AgentMessage>();
            foreach (var m in session.Messages.OfType<AgentSpeechMessage>())
            {
                seed.Add(m.SpeakerAgentId == targetAgentId
                    ? new AssistantMessage
                    {
                        Content = [new CompletionTextContent { Text = m.Text }],
                        Model = "",
                        Usage = CompletionUsage.Empty,
                        StopReason = CompletionStopReason.Stop,
                    }
                    : new UserMessage { Text = $"[From {m.SpeakerAgentId}]: {m.Text}" });
            }
            session.Messages.Add(outgoing);

            var factory = runtimeFactoryFor(targetAgentId);
            var runtime = factory.Create(seed);

            await sink.EmitTurnStartAsync(targetAgentId, targetAgentId, initiatorAgentId, ct);
            var reply = new StringBuilder();
            string error = "";
            try
            {
                await foreach (var evt in runtime.PromptAsync($"[From {initiatorAgentId}]: {message}")
                                   .WithCancellation(ct))
                {
                    switch (evt)
                    {
                        case MessageStreamEvent { Inner: CompletionTextDeltaEvent d }:
                            reply.Append(d.Delta);
                            await sink.EmitTurnDeltaAsync(d.Delta, ct);
                            break;
                        case MessageEndEvent { Message: AssistantMessage a }:
                            if (a.Error is { } e)
                                error = e;
                            if (a.StopReason == CompletionStopReason.Error && error.Length == 0)
                                error = "completion ended in error";
                            if (factory.Ledger is { } ledger)
                                await ledger.AppendAsync(new CostEntry
                                {
                                    Timestamp = DateTimeOffset.UtcNow,
                                    Model = a.Model,
                                    Channel = "chat",
                                    Peer = initiatorAgentId,
                                    InputTokens = a.Usage.Input,
                                    OutputTokens = a.Usage.Output,
                                    CacheReadTokens = a.Usage.CacheRead,
                                    CacheWriteTokens = a.Usage.CacheWrite,
                                    CostTotal = a.Usage.Cost.Total,
                                    CostInput = a.Usage.Cost.Input,
                                    CostOutput = a.Usage.Cost.Output,
                                    CostCacheRead = a.Usage.Cost.CacheRead,
                                    CostCacheWrite = a.Usage.Cost.CacheWrite,
                                });
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; }

            var replyText = reply.ToString().Trim();
            if (error.Length > 0 && replyText.Length == 0)
                replyText = $"(consult failed: {error})";

            await sink.EmitTurnEndAsync(replyText, ct);

            var incoming = new AgentSpeechMessage
            {
                SpeakerAgentId = targetAgentId,
                SpeakerDisplayName = targetAgentId,
                ToAgentId = initiatorAgentId,
                Text = replyText,
            };
            sink.BufferForInitiator(toolCallId, incoming);
            session.Messages.Add(incoming);

            await sessionStore.SaveAsync(targetAgentId, session, ct);
            return replyText;
        }
        finally
        {
            gate.Release();
        }
    }
}
