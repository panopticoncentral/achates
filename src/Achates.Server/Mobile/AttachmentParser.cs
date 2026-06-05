using System.Text.Json;
using Achates.Providers.Completions.Content;

namespace Achates.Server.Mobile;

internal static class AttachmentParser
{
    private const int MaxAttachments = 4;
    private const int MaxImageBytes = 8 * 1024 * 1024;   // 8 MB decoded
    private const int MaxPdfBytes = 32 * 1024 * 1024;    // 32 MB decoded
    private const int MaxTextBytes = 1 * 1024 * 1024;    // 1 MB decoded (inlined into the prompt)

    private static readonly HashSet<string> AllowedImageMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
    };

    private const string PdfMime = "application/pdf";

    // Text files are inlined as a text block rather than sent through the binary
    // "file" content path (which is PDF/document-parsing oriented). Any text/* type
    // qualifies, plus a few well-known textual application/* types.
    private static bool IsTextMime(string mime) =>
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mime, "application/json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mime, "application/xml", StringComparison.OrdinalIgnoreCase);

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
            var isText = !isImage && !isPdf && IsTextMime(mime);
            if (!isImage && !isPdf && !isText)
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

            var maxBytes = isPdf ? MaxPdfBytes : isText ? MaxTextBytes : MaxImageBytes;
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
            else if (isText)
            {
                // Inline the contents as a labeled, fenced text block. Text has no
                // model modality requirement, so this works with every model.
                var contents = System.Text.Encoding.UTF8.GetString(decoded);
                var label = fileName ?? "file.txt";
                var lang = FenceLanguage(mime, label);
                result.Add(new CompletionTextContent
                {
                    Text = $"Attached file \"{label}\":\n\n```{lang}\n{contents}\n```",
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

    /// <summary>
    /// Picks a Markdown fence language hint for an inlined text file, by filename
    /// extension first, then mime. Returns an empty string when nothing fits.
    /// </summary>
    private static string FenceLanguage(string mime, string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        switch (ext)
        {
            case "csv": return "csv";
            case "tsv": return "tsv";
            case "json": return "json";
            case "xml": return "xml";
            case "md" or "markdown": return "markdown";
            case "yaml" or "yml": return "yaml";
            case "html" or "htm": return "html";
        }

        if (string.Equals(mime, "text/csv", StringComparison.OrdinalIgnoreCase)) return "csv";
        if (string.Equals(mime, "application/json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (string.Equals(mime, "application/xml", StringComparison.OrdinalIgnoreCase)) return "xml";
        if (string.Equals(mime, "text/markdown", StringComparison.OrdinalIgnoreCase)) return "markdown";
        return "";
    }
}
