using System.Text.Json;
using Achates.Agent.Tools;
using SmartReader;

namespace Achates.Server.Tools;

internal sealed partial class WebTool
{
    private async Task<AgentToolResult> FetchAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var url = GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
            return TextResult("url is required.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return TextResult("Invalid URL. Only http and https are supported.");
        }

        var maxChars = Math.Clamp(GetInt(arguments, "max_chars", DefaultMaxChars), 1, MaxCharsCap);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("User-Agent", "Achates/1.0 (Web Fetch Tool)");

        HttpResponseMessage response;
        try
        {
            response = await fetchClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return TextResult("Request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return TextResult($"Request failed: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
            return TextResult($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html";

        // Read response body with size limit
        string body;
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxResponseBytes];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken);
            body = new string(buffer, 0, read);
        }
        catch (Exception ex)
        {
            return TextResult($"Failed to read response: {ex.Message}");
        }

        var extracted = contentType switch
        {
            "text/html" or "application/xhtml+xml" => ExtractReadableContent(uri, body),
            "application/json" => FormatJson(body),
            _ when contentType.StartsWith("text/") => body,
            _ => null,
        };

        if (extracted is null)
            return TextResult($"Unsupported content type: {contentType}");

        extracted = extracted.Trim();
        if (string.IsNullOrEmpty(extracted))
            return TextResult("Page returned no readable content.");

        var truncated = false;
        if (extracted.Length > maxChars)
        {
            extracted = extracted[..maxChars];
            truncated = true;
        }

        var result = ExternalContentPreamble + extracted;
        if (truncated)
            result += $"\n\n[Content truncated at {maxChars} characters]";

        return TextResult(result);
    }

    private static string ExtractReadableContent(Uri uri, string html)
    {
        try
        {
            var article = Reader.ParseArticle(uri.ToString(), html);
            if (article.IsReadable && !string.IsNullOrWhiteSpace(article.TextContent))
            {
                var title = article.Title;
                var text = article.TextContent.Trim();
                return string.IsNullOrWhiteSpace(title) ? text : $"# {title}\n\n{text}";
            }
        }
        catch
        {
            // Fall through to raw text extraction
        }

        return StripHtmlTags(html);
    }

    private static string StripHtmlTags(string html)
    {
        // Remove script and style blocks, then tags, collapse whitespace
        var cleaned = System.Text.RegularExpressions.Regex.Replace(html,
            @"<(script|style)[^>]*>[\s\S]*?</\1>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<[^>]+>", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim();
    }

    private static string FormatJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
