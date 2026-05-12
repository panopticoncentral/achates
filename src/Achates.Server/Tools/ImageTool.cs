using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Generates images using one of a configured set of image-capable models.
/// Saves to the agent's images directory.
/// </summary>
internal sealed class ImageTool : AgentTool
{
    private readonly string _agentName;
    private readonly string _agentDir;
    private readonly IReadOnlyList<string> _modelIds;
    private readonly Func<string, string, IReadOnlyList<byte[]>?, CancellationToken, Task<byte[]?>> _generateFunc;
    private readonly JsonElement _schema;

    public ImageTool(
        string agentName,
        string agentDir,
        IReadOnlyList<string> modelIds,
        Func<string, string, IReadOnlyList<byte[]>?, CancellationToken, Task<byte[]?>> generateFunc)
    {
        if (modelIds.Count == 0)
            throw new ArgumentException("At least one model id is required.", nameof(modelIds));

        _agentName = agentName;
        _agentDir = agentDir;
        _modelIds = modelIds;
        _generateFunc = generateFunc;

        var properties = new Dictionary<string, JsonElement>
        {
            ["prompt"] = StringSchema("The image generation prompt. Include style, composition, dimensions, and any other instructions."),
            ["images"] = ArraySchema(StringSchema(), "Base64-encoded reference images for refinement, style transfer, or composition guidance."),
            ["self"] = BooleanSchema("Set true to generate an image of yourself (selfie or scene featuring you). Your avatar is attached as a reference image and your appearance will be preserved."),
        };
        var required = new List<string> { "prompt" };

        if (modelIds.Count > 1)
        {
            properties["model"] = StringEnum(
                modelIds,
                $"Which model to use for this generation. Defaults to {modelIds[0]} if omitted.",
                defaultValue: modelIds[0]);
        }

        _schema = ObjectSchema(properties, required: required);
    }

    public override string Name => "image";
    public override string Description => "Generate an image from a text prompt, optionally guided by reference images. Set `self: true` to include yourself in the image.";
    public override string Label => "Image";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return TextResult("prompt is required.");

        var modelId = _modelIds[0];
        if (_modelIds.Count > 1)
        {
            var requested = GetString(arguments, "model");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                if (!_modelIds.Contains(requested))
                    return TextResult($"Unknown model '{requested}'. Available: {string.Join(", ", _modelIds)}.");
                modelId = requested;
            }
        }

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

        var includeSelf = arguments.TryGetValue("self", out var selfVal)
            && selfVal is JsonElement { ValueKind: JsonValueKind.True };

        if (includeSelf)
        {
            var avatarPath = FindAvatarPath();
            if (avatarPath is null)
                return TextResult("No avatar set. Update your profile with an avatar before generating selfies.");

            var avatarBytes = await File.ReadAllBytesAsync(avatarPath, cancellationToken);
            referenceImages ??= [];
            referenceImages.Insert(0, avatarBytes);

            prompt = "Reference image 1 is the subject's appearance — preserve face, hair, and identifying features. " + prompt;
        }

        byte[]? imageBytes;
        try
        {
            imageBytes = await _generateFunc(modelId, prompt, referenceImages, cancellationToken);
        }
        catch (Exception ex)
        {
            return TextResult($"Image generation failed: {ex.Message}");
        }

        if (imageBytes is null)
            return TextResult("The model did not return an image.");

        var imagesDir = Path.Combine(_agentDir, "images");
        Directory.CreateDirectory(imagesDir);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jpg";
        var filePath = Path.Combine(imagesDir, fileName);
        await File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken);

        var imageUrl = $"/agents/{Uri.EscapeDataString(_agentName)}/images/{Uri.EscapeDataString(fileName)}";

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = $"Generated image: {imageUrl}" }],
            ImageUrl = imageUrl,
            Details = new ImageDetails(Convert.ToBase64String(imageBytes), "image/jpeg", imageUrl),
        };
    }

    private string? FindAvatarPath()
    {
        var jpgPath = Path.Combine(_agentDir, "avatar.jpg");
        if (File.Exists(jpgPath)) return jpgPath;
        var pngPath = Path.Combine(_agentDir, "avatar.png");
        if (File.Exists(pngPath)) return pngPath;
        return null;
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
