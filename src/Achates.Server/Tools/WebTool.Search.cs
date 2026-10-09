using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;

namespace Achates.Server.Tools;

internal sealed partial class WebTool
{
    private async Task<AgentToolResult> SearchAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (!SearchAvailable)
            return TextResult("Web search is unavailable: configure tools.web_search.brave_api_key or BRAVE_API_KEY. Fetch remains available.");

        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return TextResult("query is required.");

        var count = Math.Clamp(GetInt(arguments, "count", 5), 1, 20);

        var url = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&count={count}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.Add("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await searchClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return TextResult("Search request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return TextResult($"Search request failed: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
            return TextResult($"Brave Search returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) ||
            results.GetArrayLength() == 0)
        {
            return TextResult("No results found.");
        }

        var sb = new StringBuilder();
        sb.Append(ExternalContentPreamble);

        var i = 0;
        foreach (var result in results.EnumerateArray())
        {
            i++;
            var title = result.TryGetProperty("title", out var t) ? t.GetString() : null;
            var resultUrl = result.TryGetProperty("url", out var u) ? u.GetString() : null;
            var description = result.TryGetProperty("description", out var d) ? d.GetString() : null;

            sb.AppendLine($"[{i}] {title}");
            if (resultUrl is not null)
                sb.AppendLine(resultUrl);
            if (description is not null)
                sb.AppendLine(description);
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }
}
