using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Achates.Server;

/// <summary>
/// Accepts WebSocket connections from a mapped endpoint.
/// Each connection is treated as a separate peer.
/// </summary>
public sealed class WebSocketTransport
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private int _nextPeerId;

    public string Id => "websocket";
    public string DisplayName => "WebSocket";

    public event Func<TransportMessage, Task>? MessageReceived;

    public Task SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(message.PeerId, out var ws) && ws.State == WebSocketState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(message.Text);
            return ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task SendTypingAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(peerId, out var ws) && ws.State == WebSocketState.Open)
        {
            var bytes = """{"type":"typing"}"""u8.ToArray();
            return ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync()
    {
        foreach (var (id, ws) in _connections)
        {
            if (ws.State == WebSocketState.Open)
            {
                _ = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }

            _connections.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accept a WebSocket connection and process messages until it closes.
    /// Call this from the endpoint handler.
    /// </summary>
    public async Task AcceptAsync(WebSocket ws, string? peerId = null, CancellationToken cancellationToken = default)
    {
        peerId ??= Interlocked.Increment(ref _nextPeerId).ToString();
        _connections[peerId] = ws;

        try
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (MessageReceived is { } handler)
                {
                    await handler(new TransportMessage
                    {
                        TransportId = Id,
                        PeerId = peerId,
                        Text = text,
                    });
                }
            }
        }
        finally
        {
            _connections.TryRemove(peerId, out _);
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
    }
}
