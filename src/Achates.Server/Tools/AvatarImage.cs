using SkiaSharp;

namespace Achates.Server.Tools;

/// <summary>
/// Shared avatar handling: compression and image-path resolution.
/// Used by <see cref="ProfileTool"/> and <see cref="AgentManagerTool"/>.
/// </summary>
internal static class AvatarImage
{
    public static byte[] Compress(byte[] imageBytes, int maxSize = 512, int quality = 80)
    {
        using var original = SKBitmap.Decode(imageBytes);
        if (original is null)
            return imageBytes;

        var scale = Math.Min((float)maxSize / original.Width, (float)maxSize / original.Height);
        if (scale >= 1f)
        {
            using var img = SKImage.FromBitmap(original);
            return img.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
        }

        var newWidth = (int)(original.Width * scale);
        var newHeight = (int)(original.Height * scale);
        using var resized = original.Resize(new SKImageInfo(newWidth, newHeight), SKSamplingOptions.Default);
        using var image = SKImage.FromBitmap(resized ?? original);
        return image.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
    }

    /// <summary>
    /// Resolves an image tool URL (e.g. "/agents/friday/images/file.jpg") or absolute path to a filesystem path.
    /// Returns null if the value doesn't look like a path.
    /// </summary>
    public static string? TryResolveImagePath(string value, string agentDir)
    {
        // Absolute filesystem path
        if (value.StartsWith('/') && !value.StartsWith("/agents/"))
            return value;

        // Relative URL from ImageTool: /agents/{name}/images/{file}
        if (value.StartsWith("/agents/") && value.Contains("/images/"))
        {
            var fileName = value[(value.LastIndexOf('/') + 1)..];
            return Path.Combine(agentDir, "images", fileName);
        }

        // Looks like a path if it ends with an image extension
        if (value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(agentDir, "images", value);

        return null;
    }
}
