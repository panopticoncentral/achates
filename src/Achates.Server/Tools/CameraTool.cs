using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Captures a photo from the user's mobile device camera.
/// </summary>
internal sealed class CameraTool(DeviceCommandBridge bridge) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(new Dictionary<string, JsonElement>
    {
        ["facing"] = StringEnum(["back", "front"], "Camera direction."),
    });

    public override string Name => "camera";
    public override string Description => "Take a photo using the user's device camera. Returns the captured image.";
    public override string Label => "Taking Photo";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        if (!bridge.IsAvailable("camera"))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Camera is not available. The user's device is not connected or does not support camera access." }],
            };
        }

        var facing = GetString(arguments, "facing") ?? "back";
        var parameters = JsonSerializer.SerializeToElement(new { facing });

        var result = await bridge.InvokeAsync("device.camera", parameters, timeout: TimeSpan.FromSeconds(30), ct: cancellationToken);
        if (result is null)
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Failed to capture photo from the device. The request timed out or was rejected." }],
            };
        }

        // Expect result to contain { "data": "<base64>", "mime_type": "image/jpeg" }
        var payload = result.Value;
        var data = payload.TryGetProperty("data", out var d) ? d.GetString() : null;
        var mimeType = payload.TryGetProperty("mime_type", out var m) ? m.GetString() : "image/jpeg";

        if (data is null)
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Device returned an invalid camera response (missing image data)." }],
            };
        }

        return new AgentToolResult
        {
            Content = [new CompletionImageContent { Data = data, MimeType = mimeType ?? "image/jpeg" }],
        };
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
