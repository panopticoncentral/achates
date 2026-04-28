using System.Text.Json;
using Achates.Providers.Completions.Content;

namespace Achates.Server.Mobile;

internal static class AttachmentParser
{
    private const int MaxAttachments = 4;
    private const int MaxBytesPerAttachment = 8 * 1024 * 1024; // 8 MB decoded
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
    };

    /// <summary>
    /// Parses the optional <c>attachments</c> array from a chat.send params object.
    /// Returns a (possibly empty) list of content blocks on success, or null with
    /// <paramref name="error"/> populated on failure.
    /// </summary>
    public static List<CompletionUserContent>? Parse(JsonElement parameters, out string? error)
    {
        error = null;

        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("attachments", out var arr))
        {
            return [];
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            error = "'attachments' must be an array.";
            return null;
        }

        var count = arr.GetArrayLength();
        if (count > MaxAttachments)
        {
            error = $"Too many attachments (max {MaxAttachments}).";
            return null;
        }

        var result = new List<CompletionUserContent>(count);
        foreach (var element in arr.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "Each attachment must be an object.";
                return null;
            }

            if (!element.TryGetProperty("mime", out var mimeProp) ||
                mimeProp.ValueKind != JsonValueKind.String)
            {
                error = "Attachment is missing 'mime' string.";
                return null;
            }

            if (!element.TryGetProperty("data", out var dataProp) ||
                dataProp.ValueKind != JsonValueKind.String)
            {
                error = "Attachment is missing 'data' string.";
                return null;
            }

            var mime = mimeProp.GetString()!;
            if (!AllowedMimeTypes.Contains(mime))
            {
                error = $"Unsupported attachment mime type '{mime}'.";
                return null;
            }

            var data = dataProp.GetString()!;
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                error = "Attachment 'data' is not valid base64.";
                return null;
            }

            if (decoded.Length > MaxBytesPerAttachment)
            {
                error = "Attachment too large (max 8 MB).";
                return null;
            }

            result.Add(new CompletionImageContent
            {
                Data = data,
                MimeType = mime,
            });
        }

        return result;
    }
}
