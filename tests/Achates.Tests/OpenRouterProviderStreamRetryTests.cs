using System.Net;
using System.Text;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Providers.OpenRouter;

namespace Achates.Tests;

/// <summary>
/// Tests for <see cref="OpenRouterProvider"/>'s mid-stream recovery: when an SSE
/// stream dies AFTER yielding content (a stall the upstream provider reports as a
/// transient error, or a client-side idle-read timeout), the provider discards the
/// partial assistant message and re-streams the turn from scratch. This complements
/// the client's pre-yield handshake retry (which can't recover post-yield because it
/// has already handed chunks to the consumer).
/// </summary>
public class OpenRouterProviderStreamRetryTests : IDisposable
{
    private readonly Func<int, TimeSpan> _originalDelay;
    private readonly TimeSpan _originalIdle;

    public OpenRouterProviderStreamRetryTests()
    {
        _originalDelay = OpenRouterClient.RetryDelay;
        _originalIdle = OpenRouterClient.StreamIdleTimeout;
        OpenRouterClient.RetryDelay = _ => TimeSpan.FromMilliseconds(10);
    }

    public void Dispose()
    {
        OpenRouterClient.RetryDelay = _originalDelay;
        OpenRouterClient.StreamIdleTimeout = _originalIdle;
    }

    private const string ChunkLine =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}";

    private const string ValidStreamBody = ChunkLine + "\n\ndata: [DONE]\n\n";

    // A chunk made it through, then the upstream reports a transient 502 mid-stream.
    private const string ChunkThenInline502 =
        ChunkLine + "\n\ndata: {\"error\":{\"code\":502,\"message\":\"Upstream idle timeout exceeded\",\"metadata\":{\"error_type\":\"provider_unavailable\"}}}\n\n";

    // A chunk made it through, then a NON-transient error (bad request) — must NOT retry.
    private const string ChunkThenInline400 =
        ChunkLine + "\n\ndata: {\"error\":{\"code\":400,\"message\":\"bad request\"}}\n\n";

    private static HttpResponseMessage StreamOk(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    // Returns one valid chunk then stalls forever (until the read is cancelled),
    // exercising the client-side idle-read timeout.
    private static HttpResponseMessage StreamStallAfterChunk() =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallStream(ChunkLine + "\n\n")),
        };

    private sealed class QueuingHttpHandler(params Func<HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more responses queued.");
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    /// <summary>A read-only stream that returns a fixed prefix then blocks until cancelled.</summary>
    private sealed class StallStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _pos;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _pos);
                _prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0; // unreachable
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (OpenRouterProvider Provider, Model Model) MakeProvider(QueuingHttpHandler handler)
    {
        var provider = new OpenRouterProvider
        {
            HttpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example/api/v1/") },
            Key = "test-key",
        };
        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            Provider = provider,
            Cost = new ModelCost { Prompt = 0, Completion = 0 },
            ContextWindow = 8000,
            Input = ModelModalities.Text,
            Output = ModelModalities.Text,
            Parameters = ModelParameters.Temperature,
        };
        return (provider, model);
    }

    private static CompletionContext Ctx() => new()
    {
        Messages = [new CompletionUserTextMessage { Text = "hi" }],
    };

    private static async Task<CompletionAssistantMessage> DrainAsync(CompletionEventStream stream)
    {
        await foreach (var _ in stream) { /* drain events */ }
        return await stream.ResultAsync;
    }

    [Fact]
    public async Task TransientErrorAfterChunk_RetriesAndRecovers()
    {
        var handler = new QueuingHttpHandler(
            () => StreamOk(ChunkThenInline502),
            () => StreamOk(ValidStreamBody));
        var (provider, model) = MakeProvider(handler);

        var result = await DrainAsync(provider.GetCompletions(model, Ctx()));

        Assert.Equal(CompletionStopReason.Stop, result.CompletionStopReason);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(2, handler.CallCount); // replayed the turn once
    }

    [Fact]
    public async Task TransientErrorEveryAttempt_ExhaustsAndEmitsError()
    {
        var handler = new QueuingHttpHandler(
            () => StreamOk(ChunkThenInline502),
            () => StreamOk(ChunkThenInline502),
            () => StreamOk(ChunkThenInline502));
        var (provider, model) = MakeProvider(handler);

        var result = await DrainAsync(provider.GetCompletions(model, Ctx()));

        Assert.Equal(CompletionStopReason.Error, result.CompletionStopReason);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Equal(3, handler.CallCount); // capped at 3 attempts
    }

    [Fact]
    public async Task NonTransientErrorAfterChunk_DoesNotRetry()
    {
        var handler = new QueuingHttpHandler(
            () => StreamOk(ChunkThenInline400));
        var (provider, model) = MakeProvider(handler);

        var result = await DrainAsync(provider.GetCompletions(model, Ctx()));

        Assert.Equal(CompletionStopReason.Error, result.CompletionStopReason);
        Assert.Equal(1, handler.CallCount); // no retry on a non-transient error
    }

    [Fact]
    public async Task IdleStallAfterChunk_TimesOutRetriesAndRecovers()
    {
        // This is the Claire case: the stream yields a little, then the upstream
        // goes silent. The client-side idle-read timeout fires fast (instead of
        // waiting OpenRouter's ~5 min), the provider replays, and it recovers.
        OpenRouterClient.StreamIdleTimeout = TimeSpan.FromMilliseconds(150);

        var handler = new QueuingHttpHandler(
            StreamStallAfterChunk,
            () => StreamOk(ValidStreamBody));
        var (provider, model) = MakeProvider(handler);

        var result = await DrainAsync(provider.GetCompletions(model, Ctx()));

        Assert.Equal(CompletionStopReason.Stop, result.CompletionStopReason);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task IdleStall_SurfacesAsStreamIdleTimeout_AtClientLayer()
    {
        // Isolate the mechanism: a stalled stream throws StreamIdleTimeoutException
        // from the client iterator after yielding its first chunk.
        OpenRouterClient.StreamIdleTimeout = TimeSpan.FromMilliseconds(150);

        var handler = new QueuingHttpHandler(StreamStallAfterChunk);
        var client = new OpenRouterClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example/api/v1/") }, "k");

        var chunks = 0;
        await Assert.ThrowsAsync<StreamIdleTimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamOpenRouterChatCompletionAsync(new()
            {
                Model = "m",
                Messages = [new() { Role = "user" }],
                Stream = true,
            }))
            {
                chunks++;
            }
        });

        Assert.Equal(1, chunks); // the pre-stall chunk made it through
    }
}
