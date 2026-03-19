using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Achates.Server.Mobile;

/// <summary>
/// Per-connection state for a WebSocket client.
/// Manages RPC correlation, event sequencing, and frame sending.
/// </summary>
public sealed class MobileConnection(WebSocket socket, string connectionId, ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseFrame>> _pendingRequests = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<MobileConnection>();
    private long _seq;

    public string ConnectionId => connectionId;
    public WebSocket Socket => socket;
    public HashSet<string> Capabilities { get; } = [];

    /// <summary>
    /// Send a response frame to the client.
    /// </summary>
    public async Task SendResponseAsync(ResponseFrame frame, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(frame, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>
    /// Send an event frame to the client with an auto-incrementing sequence number.
    /// </summary>
    public async Task SendEventAsync(string eventName, object? payload = null, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var payloadElement = payload is not null
            ? JsonSerializer.SerializeToElement(payload, JsonOptions)
            : default;

        var frame = new EventFrame
        {
            Event = eventName,
            Payload = payloadElement,
            Seq = seq,
        };

        var json = JsonSerializer.Serialize(frame, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>
    /// Send a request to the client and wait for a response (for device commands).
    /// </summary>
    public async Task<ResponseFrame> SendRequestAsync(string method, object? parameters = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<ResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            var paramsElement = parameters is not null
                ? JsonSerializer.SerializeToElement(parameters, JsonOptions)
                : default;

            var frame = new RequestFrame
            {
                Id = id,
                Method = method,
                Params = paramsElement,
            };

            var json = JsonSerializer.Serialize(frame, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);

            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Complete a pending server-to-client request with a response from the client.
    /// </summary>
    public bool CompleteRequest(string id, ResponseFrame response)
    {
        if (_pendingRequests.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(response);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cancel pending requests on disconnect.
    /// </summary>
    public void Dispose()
    {
        foreach (var tcs in _pendingRequests.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }
}
