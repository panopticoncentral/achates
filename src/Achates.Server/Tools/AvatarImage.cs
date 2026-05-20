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
    /// Resolves an image tool URL (e.g. "/agents/friday/images/file.jpg") or absolute path to a
    /// filesystem path. Image-tool URLs are resolved relative to the agent named *in the URL*
    /// (which owns the file), not the <paramref name="agentDir"/> passed in — so an agent can set
    /// another agent's avatar from an image it generated. Returns null if the value doesn't look
    /// like a path.
    /// </summary>
    public static string? TryResolveImagePath(string value, string agentDir)
    {
        // Absolute filesystem path
        if (value.StartsWith('/') && !value.StartsWith("/agents/"))
            return value;

        // Relative URL from ImageTool: /agents/{name}/images/{file}
        if (value.StartsWith("/agents/") && value.Contains("/images/"))
        {
            var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Expect exactly ["agents", "{name}", "images", "{file}"]. Names are filesystem-safe
            // (NormalizeId) and filenames have no slashes, so anything else is malformed.
            if (segments.Length == 4 && segments[0] == "agents" && segments[2] == "images")
            {
                var urlAgentName = Uri.UnescapeDataString(segments[1]);
                var fileName = Uri.UnescapeDataString(segments[3]);

                if (!IsSafeSegment(urlAgentName) || !IsSafeSegment(fileName))
                    return null;

                var agentsRoot = Path.GetDirectoryName(agentDir);
                return agentsRoot is null
                    ? Path.Combine(agentDir, "images", fileName)
                    : Path.Combine(agentsRoot, urlAgentName, "images", fileName);
            }

            return null;
        }

        // Looks like a path if it ends with an image extension
        if (value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(agentDir, "images", value);

        return null;
    }

    private static bool IsSafeSegment(string segment) =>
        segment.Length > 0 &&
        segment != ".." &&
        !segment.Contains("..") &&
        segment.IndexOf('/') < 0 &&
        segment.IndexOf('\\') < 0;
}
