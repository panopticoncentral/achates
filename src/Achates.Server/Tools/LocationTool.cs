using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Gets the user's current GPS location via the mobile device.
/// </summary>
internal sealed class LocationTool(DeviceCommandBridge bridge) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema([]);

    public override string Name => "location";
    public override string Description => "Get the user's current GPS location including latitude, longitude, and accuracy.";
    public override string Label => "Getting Location";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        if (!bridge.IsAvailable("location"))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Location is not available. The user's device is not connected or does not support location services." }],
            };
        }

        var result = await bridge.InvokeAsync("device.location", timeout: TimeSpan.FromSeconds(15), ct: cancellationToken);
        if (result is null)
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Failed to get location from the device. The request timed out or was rejected." }],
            };
        }

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = result.Value.ToString() }],
        };
    }
}
