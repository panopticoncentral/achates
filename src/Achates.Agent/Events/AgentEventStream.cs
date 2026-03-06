using System.Threading.Channels;
using Achates.Agent.Messages;

namespace Achates.Agent.Events;

/// <summary>
/// A push-based async event stream for agent lifecycle events.
/// Consumers iterate via <see cref="IAsyncEnumerable{T}"/> or await <see cref="ResultAsync"/>
/// for the complete list of new messages.
/// </summary>
public sealed class AgentEventStream : IAsyncEnumerable<AgentEvent>
{
    private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleWriter = true });
    private readonly TaskCompletionSource<IReadOnlyList<AgentMessage>> _resultTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<AgentEvent, Task>? _onEvent;

    internal AgentEventStream(Func<AgentEvent, Task>? onEvent = null)
    {
        _onEvent = onEvent;
    }

    internal void Push(AgentEvent evt)
    {
        _channel.Writer.TryWrite(evt);
        // Fire-and-forget subscriber notification — subscribers run inline
        // with the loop but errors are swallowed to avoid breaking the stream.
        if (_onEvent is not null)
        {
            try { _onEvent(evt).GetAwaiter().GetResult(); }
            catch { /* subscriber errors must not break the agent loop */ }
        }
    }

    internal void End(IReadOnlyList<AgentMessage> newMessages)
    {
        _resultTcs.TrySetResult(newMessages);
        _channel.Writer.TryComplete();
    }

    internal void Fault(Exception exception)
    {
        _resultTcs.TrySetException(exception);
        _channel.Writer.TryComplete(exception);
    }

    public async IAsyncEnumerator<AgentEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Await the list of all new messages produced during this agent run.
    /// </summary>
    public Task<IReadOnlyList<AgentMessage>> ResultAsync => _resultTcs.Task;
}
