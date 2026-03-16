using System.Text.Json;

namespace Achates.Server.Mobile;

/// <summary>
/// Bridge between server-side tools and mobile device capabilities.
/// Routes device command requests through the active mobile connection.
/// </summary>
public sealed class DeviceCommandBridge
{
    private MobileTransport? _transport;

    public void SetTransport(MobileTransport transport) => _transport = transport;

    public bool IsAvailable(string capability)
    {
        var conn = _transport?.ActiveConnection;
        return conn is not null && conn.Capabilities.Contains(capability);
    }

    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? parameters = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var conn = _transport?.ActiveConnection;
        if (conn is null) return null;

        try
        {
            var response = await conn.SendRequestAsync(method, parameters, timeout, ct);
            if (!response.Ok) return null;
            return response.Payload;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
