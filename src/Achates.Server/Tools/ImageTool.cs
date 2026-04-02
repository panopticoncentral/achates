using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Providers.Models;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Generates images using an image-capable model. Saves to the agent's images directory.
/// </summary>
internal sealed class ImageTool(
    string agentName,
    string agentDir,
    Func<string, string, IReadOnlyList<byte[]>?, CancellationToken, Task<byte[]?>> generateFunc,
    Func<ModelModalities?, CancellationToken, Task<IReadOnlyList<Model>>> listModelsFunc) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["generate", "models"],
                "Action: generate (create an image), models (list available image models)."),
            ["model"] = StringSchema("The image model ID to use. Required for generate."),
            ["prompt"] = StringSchema("The image generation prompt. Include style, composition, dimensions, and any other instructions. Required for generate."),
            ["images"] = ArraySchema(StringSchema(), "Base64-encoded reference images for refinement, style transfer, or composition guidance."),
        },
        required: ["action"]);

    public override string Name => "image";
    public override string Description => "Generate images or list available image models. Use action 'models' first to discover model IDs, then 'generate' to create images.";
    public override string Label => "Image";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "generate";

        return action switch
        {
            "models" => await ListModelsAsync(cancellationToken),
            "generate" => await GenerateAsync(arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        var imageModels = (await listModelsFunc(ModelModalities.Image, cancellationToken)).ToList();

        if (imageModels.Count == 0)
            return TextResult("No image-capable models available.");

        var sb = new StringBuilder();
        sb.AppendLine($"{imageModels.Count} image-capable model(s):");
        sb.AppendLine();
        foreach (var m in imageModels)
        {
            sb.AppendLine($"- **{m.Id}** ({m.Name})");
            if (m.Description is { Length: > 0 } desc)
                sb.AppendLine($"  {desc}");
            if (m.Cost.ImageOutput is { } imgCost and > 0)
                sb.AppendLine($"  Output image cost: ${imgCost:G4}/image");
            else if (m.Cost.Completion > 0)
                sb.AppendLine($"  Completion cost: ${m.Cost.Completion * 1_000_000:F2}/M tokens");
        }

        return TextResult(sb.ToString());
    }

    private async Task<AgentToolResult> GenerateAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var model = GetString(arguments, "model");
        if (string.IsNullOrWhiteSpace(model))
            return TextResult("model is required for generate action.");

        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return TextResult("prompt is required for generate action.");

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

        var imagesDir = Path.Combine(agentDir, "images");
        Directory.CreateDirectory(imagesDir);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jpg";
        var filePath = Path.Combine(imagesDir, fileName);
        await File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken);

        var imageUrl = $"/agents/{Uri.EscapeDataString(agentName)}/images/{Uri.EscapeDataString(fileName)}";

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = $"Generated image: {imageUrl}" }],
            ImageUrl = imageUrl,
            Details = new ImageDetails(Convert.ToBase64String(imageBytes), "image/jpeg", imageUrl),
        };
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}

/// <summary>
/// Image data for UI delivery (not sent to the model).
/// </summary>
internal sealed record ImageDetails(string Data, string MimeType, string Url);
