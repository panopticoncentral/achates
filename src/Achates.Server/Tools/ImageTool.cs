using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Generates images using an image-capable model.
/// </summary>
internal sealed class ImageTool(
    Func<string, string, IReadOnlyList<byte[]>?, CancellationToken, Task<byte[]?>> generateFunc) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["model"] = StringSchema("The image model ID to use (e.g. google/gemini-2.5-flash-image). Use the models tool or ask the user to discover available image models."),
            ["prompt"] = StringSchema("The image generation prompt. Include style, composition, dimensions, and any other instructions."),
            ["images"] = ArraySchema(StringSchema(), "Base64-encoded reference images for refinement, style transfer, or composition guidance."),
        },
        required: ["model", "prompt"]);

    public override string Name => "image";
    public override string Description => "Generate an image using an image-capable model. Include dimensions, style, and composition instructions in the prompt.";
    public override string Label => "Image";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var model = GetString(arguments, "model");
        if (string.IsNullOrWhiteSpace(model))
            return TextResult("model is required.");

        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return TextResult("prompt is required.");

        List<byte[]>? referenceImages = null;
        if (arguments.TryGetValue("images", out var imagesVal) && imagesVal is JsonElement { ValueKind: JsonValueKind.Array } imagesArray)
        {
            referenceImages = [];
            foreach (var item in imagesArray.EnumerateArray())
            {
                var base64 = item.GetString();
                if (base64 is { Length: > 0 })
                    referenceImages.Add(Convert.FromBase64String(base64));
            }

            if (referenceImages.Count == 0)
                referenceImages = null;
        }

        byte[]? imageBytes;
        try
        {
            imageBytes = await generateFunc(model, prompt, referenceImages, cancellationToken);
        }
        catch (Exception ex)
        {
            return TextResult($"Image generation failed: {ex.Message}");
        }

        if (imageBytes is null)
            return TextResult("The model did not return an image.");

        return new AgentToolResult
        {
            Content = [new CompletionImageContent { Data = Convert.ToBase64String(imageBytes), MimeType = "image/jpeg" }],
        };
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
