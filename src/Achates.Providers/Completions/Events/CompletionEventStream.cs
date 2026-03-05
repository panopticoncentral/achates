using System.Threading.Channels;
using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

/// <summary>
/// A push-based async event stream for assistant message generation.
/// Providers push events via <see cref="Push"/> and consumers iterate via <see cref="IAsyncEnumerable{T}"/>.
/// Use <see cref="ResultAsync"/> to await the final <see cref="CompletionAssistantMessage"/>.
/// </summary>
public sealed class CompletionEventStream : IAsyncEnumerable<CompletionEvent>
{
    private readonly Channel<CompletionEvent> _channel = Channel.CreateUnbounded<CompletionEvent>(
        new UnboundedChannelOptions { SingleWriter = true });
    private readonly TaskCompletionSource<CompletionAssistantMessage> _resultTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Creates a stream and starts the producer in the background.
    /// The producer receives the stream to push events into; if it throws
    /// without calling <see cref="End"/>, the stream is faulted automatically.
    /// </summary>
    public static CompletionEventStream Create(Func<CompletionEventStream, Task> producer)
    {
        var stream = new CompletionEventStream();
        _ = RunProducerAsync(stream, producer);
        return stream;

        static async Task RunProducerAsync(CompletionEventStream stream, Func<CompletionEventStream, Task> producer)
        {
            try
            {
                await producer(stream).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                stream.Fault(ex);
            }
        }
    }

    /// <summary>
    /// Push an event into the stream. Called by providers during generation.
    /// </summary>
    public void Push(CompletionEvent evt)
    {
        switch (evt)
        {
            case CompletionDoneEvent done:
                _resultTcs.TrySetResult(done.CompletionMessage);
                break;
            case CompletionErrorEvent error:
                _resultTcs.TrySetResult(error.Error);
                break;
        }

        _channel.Writer.TryWrite(evt);
    }

    /// <summary>
    /// Signal that no more events will be pushed.
    /// </summary>
    public void End()
    {
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Signal an exception occurred during generation.
    /// </summary>
    public void Fault(Exception exception)
    {
        _resultTcs.TrySetException(exception);
        _channel.Writer.TryComplete(exception);
    }

    /// <summary>
    /// Iterate over all events as they arrive.
    /// </summary>
    public async IAsyncEnumerator<CompletionEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Await the final <see cref="CompletionAssistantMessage"/> once the stream completes.
    /// </summary>
    public Task<CompletionAssistantMessage> ResultAsync => _resultTcs.Task;
}
