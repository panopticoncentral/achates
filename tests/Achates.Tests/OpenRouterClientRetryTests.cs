using System.Net;
using System.Text;
using Achates.Providers.OpenRouter;
using Achates.Providers.OpenRouter.Chat;

namespace Achates.Tests;

public class OpenRouterClientRetryTests : IDisposable
{
    private readonly Func<int, TimeSpan> _originalDelay;

    public OpenRouterClientRetryTests()
    {
        _originalDelay = OpenRouterClient.RetryDelay;
        OpenRouterClient.RetryDelay = _ => TimeSpan.FromMilliseconds(10);
    }

    public void Dispose() => OpenRouterClient.RetryDelay = _originalDelay;

    private const string ValidStreamBody =
        """
        data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"test","choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":null}]}

        data: [DONE]

        """;

    private const string Inline502Body =
        """
        data: {"error":{"code":502,"message":"JSON error injected into SSE stream."}}

        """;

    private const string ChunkThenInline502 =
        """
        data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"test","choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":null}]}

        data: {"error":{"code":502,"message":"JSON error injected into SSE stream."}}

        """;

    // Non-502 code but metadata flags provider_unavailable — exercises the
    // metadata-only branch of IsTransientServerError.
    private const string InlineProviderUnavailableBody =
        """
        data: {"error":{"code":429,"message":"upstream busy","metadata":{"error_type":"provider_unavailable"}}}

        """;

    private static HttpResponseMessage StreamOk(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static HttpResponseMessage Error(HttpStatusCode status, int errorCode) =>
        new(status)
        {
            Content = new StringContent(
                $"{{\"error\":{{\"code\":{errorCode},\"message\":\"err\"}}}}",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class QueuingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int CallCount { get; private set; }

        public QueuingHttpHandler(params Func<HttpResponseMessage>[] responses)
            => _responses = new Queue<Func<HttpResponseMessage>>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more responses queued.");
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private static OpenRouterChatCompletionRequest StubRequest() => new()
    {
        Model = "test-model",
        Messages = [new OpenRouterChatMessage { Role = "user" }],
        Stream = true,
    };

    private static OpenRouterClient MakeClient(QueuingHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example/api/v1/") }, "test-key");

    [Fact]
    public async Task StreamingHappyPath_YieldsChunksAndStops()
    {
        var handler = new QueuingHttpHandler(() => StreamOk(ValidStreamBody));
        var client = MakeClient(handler);

        var chunks = new List<OpenRouterChatCompletionChunk>();
        await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("hi", chunks[0].Choices[0].Delta.Content);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Streaming_502OnHandshake_RetriesAndSucceeds()
    {
        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.BadGateway, 502),
            () => StreamOk(ValidStreamBody));
        var client = MakeClient(handler);

        var chunks = new List<OpenRouterChatCompletionChunk>();
        await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Streaming_502InlineBeforeFirstChunk_RetriesAndSucceeds()
    {
        var handler = new QueuingHttpHandler(
            () => StreamOk(Inline502Body),
            () => StreamOk(ValidStreamBody));
        var client = MakeClient(handler);

        var chunks = new List<OpenRouterChatCompletionChunk>();
        await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Streaming_ProviderUnavailableMetadata_RetriesAndSucceeds()
    {
        // Code is 429 (not 502), but metadata.error_type == "provider_unavailable"
        // — IsTransientServerError should still match via the metadata branch.
        var handler = new QueuingHttpHandler(
            () => StreamOk(InlineProviderUnavailableBody),
            () => StreamOk(ValidStreamBody));
        var client = MakeClient(handler);

        var chunks = new List<OpenRouterChatCompletionChunk>();
        await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(HttpStatusCode.GatewayTimeout, 504)]
    public async Task Streaming_5xxOnHandshake_RetriesAndSucceeds(
        HttpStatusCode status, int errorCode)
    {
        // Observed in the wild: OpenRouter returns a bare 500 "Internal Server
        // Error" (no metadata) on the initial handshake. Any 5xx before the
        // first chunk is idempotent to replay and should be retried.
        var handler = new QueuingHttpHandler(
            () => Error(status, errorCode),
            () => StreamOk(ValidStreamBody));
        var client = MakeClient(handler);

        var chunks = new List<OpenRouterChatCompletionChunk>();
        await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_500OnFirstAttempt_RetriesAndSucceeds()
    {
        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.InternalServerError, 500),
            () => CompletionOk());
        var client = MakeClient(handler);

        var response = await client.CreateOpenRouterChatCompletionAsync(StubRequest());

        Assert.NotNull(response);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Streaming_502InlineAfterFirstChunk_ThrowsWithoutRetry()
    {
        var handler = new QueuingHttpHandler(() => StreamOk(ChunkThenInline502));
        var client = MakeClient(handler);

        var chunks = new List<OpenRouterChatCompletionChunk>();
        var ex = await Assert.ThrowsAsync<OpenRouterException>(async () =>
        {
            await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
                chunks.Add(chunk);
        });

        Assert.Single(chunks);           // first chunk made it through
        Assert.Equal(502, ex.Code);
        Assert.Equal(1, handler.CallCount); // no retry
    }

    [Fact]
    public async Task Streaming_ThreeConsecutive502s_ThrowsAfterRetries()
    {
        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.BadGateway, 502),
            () => Error(HttpStatusCode.BadGateway, 502),
            () => Error(HttpStatusCode.BadGateway, 502));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<OpenRouterException>(async () =>
        {
            await foreach (var _ in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            {
                // drain
            }
        });

        Assert.Equal(502, ex.Code);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Streaming_429_ThrowsWithoutRetry()
    {
        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.TooManyRequests, 429));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<OpenRouterException>(async () =>
        {
            await foreach (var _ in client.StreamOpenRouterChatCompletionAsync(StubRequest()))
            {
                // drain
            }
        });

        Assert.Equal(429, ex.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Streaming_CancellationDuringBackoff_ThrowsOperationCanceled()
    {
        // Stretch backoff so the deterministic CancelAfter window below
        // (200ms) lands the cancellation squarely inside Task.Delay.
        OpenRouterClient.RetryDelay = _ => TimeSpan.FromSeconds(30);

        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.BadGateway, 502),
            () => StreamOk(ValidStreamBody));
        var client = MakeClient(handler);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamOpenRouterChatCompletionAsync(
                StubRequest(), cts.Token))
            {
                // drain
            }
        });
    }

    private static HttpResponseMessage CompletionOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """
            {"id":"r1","model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
            """,
            Encoding.UTF8,
            "application/json"),
    };

    [Fact]
    public async Task NonStreaming_502OnFirstAttempt_RetriesAndSucceeds()
    {
        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.BadGateway, 502),
            () => CompletionOk());
        var client = MakeClient(handler);

        var response = await client.CreateOpenRouterChatCompletionAsync(StubRequest());

        Assert.NotNull(response);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_ThreeConsecutive502s_ThrowsAfterRetries()
    {
        var handler = new QueuingHttpHandler(
            () => Error(HttpStatusCode.BadGateway, 502),
            () => Error(HttpStatusCode.BadGateway, 502),
            () => Error(HttpStatusCode.BadGateway, 502));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<OpenRouterException>(
            () => client.CreateOpenRouterChatCompletionAsync(StubRequest()));

        Assert.Equal(502, ex.Code);
        Assert.Equal(3, handler.CallCount);
    }
}
