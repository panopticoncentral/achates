using System.Text.Json;

namespace Achates.Server.Mobile;

/// <summary>
/// Bridge between server-side tools and mobile device capabilities.
/// Routes device command requests through any connected client that has the capability.
/// </summary>
public sealed class DeviceCommandBridge
{
    private MobileTransport? _transport;

    public void SetTransport(MobileTransport transport) => _transport = transport;

    public bool IsAvailable(string capability)
    {
        return _transport?.Connections.Any(c => c.Capabilities.Contains(capability)) == true;
    }

    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? parameters = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var conn = _transport?.Connections.FirstOrDefault(c =>
        {
            // Pick a connected client — prefer one with relevant capabilities
            var capability = method switch
            {
                "device.location" => "location",
                "device.camera" => "camera",
                _ => null,
            };
            return capability is null || c.Capabilities.Contains(capability);
        });

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
