using System.Text.Json;
using Achates.Providers.Completions.Content;

namespace Achates.Server.Mobile;

internal static class AttachmentParser
{
    private const int MaxAttachments = 4;
    private const int MaxImageBytes = 8 * 1024 * 1024;   // 8 MB decoded
    private const int MaxPdfBytes = 32 * 1024 * 1024;    // 32 MB decoded

    private static readonly HashSet<string> AllowedImageMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
    };

    private const string PdfMime = "application/pdf";

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
            var isImage = AllowedImageMimes.Contains(mime);
            var isPdf = string.Equals(mime, PdfMime, StringComparison.OrdinalIgnoreCase);
            if (!isImage && !isPdf)
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

            var maxBytes = isPdf ? MaxPdfBytes : MaxImageBytes;
            if (decoded.Length > maxBytes)
            {
                var maxMb = maxBytes / (1024 * 1024);
                error = $"Attachment too large (max {maxMb} MB for {mime}).";
                return null;
            }

            string? fileName = null;
            if (element.TryGetProperty("filename", out var fnProp) &&
                fnProp.ValueKind == JsonValueKind.String)
            {
                fileName = fnProp.GetString();
            }

            if (isPdf)
            {
                result.Add(new CompletionFileContent
                {
                    Data = data,
                    MimeType = mime,
                    FileName = fileName ?? "document.pdf",
                });
            }
            else
            {
                result.Add(new CompletionImageContent
                {
                    Data = data,
                    MimeType = mime,
                });
            }
        }

        return result;
    }
}
