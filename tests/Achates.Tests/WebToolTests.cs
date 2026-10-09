using System.Net;
using System.Text;
using System.Text.Json;
using Achates.Providers.Completions.Content;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class WebToolTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(send(request));
        }
    }

    private static HttpResponseMessage Response(string body, string mime = "text/plain") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mime) };
    private static async Task<string> Run(WebTool tool, string arguments) =>
        Assert.IsType<CompletionTextContent>(Assert.Single((await tool.ExecuteAsync("call",
            JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!)).Content)).Text;

    [Fact]
    public async Task Search_uses_Brave_and_does_not_fetch_results()
    {
        using var search = new Handler(request =>
        {
            Assert.Equal("api.search.brave.com", request.RequestUri!.Host);
            Assert.Contains("q=hello%20world", request.RequestUri.Query);
            Assert.Contains("count=20", request.RequestUri.Query);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Subscription-Token")));
            return Response("""{"web":{"results":[{"title":"Example","url":"https://example.test/article","description":"Snippet"}]}}""", "application/json");
        });
        using var fetch = new Handler(_ => throw new Exception("Search must not fetch a result"));
        using var searchClient = new HttpClient(search);
        using var fetchClient = new HttpClient(fetch);
        var result = await Run(new WebTool("test-key", searchClient, fetchClient),
            """{"action":"search","query":"hello world","count":99}""");
        Assert.Contains("untrusted", result);
        Assert.Contains("[1] Example", result);
        Assert.Contains("https://example.test/article", result);
        Assert.Contains("Snippet", result);
        Assert.Equal(1, search.Requests);
        Assert.Equal(0, fetch.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Fetch_works_without_search_credentials(string? key)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("https://example.test/article", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("X-Subscription-Token"));
            return Response("Hello world");
        });
        using var client = new HttpClient(handler);
        var tool = new WebTool(key, client, client);
        Assert.Contains("unavailable", tool.Description);
        var search = await Run(tool, """{"action":"search","query":"hello"}""");
        Assert.Contains("Fetch remains available", search);
        Assert.Equal(0, handler.Requests);
        var fetch = await Run(tool, """{"action":"fetch","url":"https://example.test/article","max_chars":5}""");
        Assert.Contains("untrusted", fetch);
        Assert.Contains("Hello", fetch);
        Assert.Contains("truncated at 5", fetch);
        Assert.DoesNotContain("world", fetch);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("{}", "action must")]
    [InlineData("{\"action\":\"other\"}", "action must")]
    [InlineData("{\"action\":\"search\"}", "query is required")]
    [InlineData("{\"action\":\"fetch\"}", "url is required")]
    [InlineData("{\"action\":\"fetch\",\"url\":\"file:///etc/hosts\"}", "Invalid URL")]
    public async Task Invalid_actions_and_missing_parameters_do_not_send_requests(string args, string error)
    {
        using var handler = new Handler(_ => throw new Exception("Unexpected request"));
        using var client = new HttpClient(handler);
        Assert.Contains(error, await Run(new WebTool("key", client, client), args));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("text/html", "<html><body><p>Article text</p><script>hidden()</script></body></html>", "Article text")]
    [InlineData("application/json", "{\"value\":42}", "42")]
    public async Task Fetch_preserves_content_extraction(string mime, string body, string expected)
    {
        using var handler = new Handler(_ => Response(body, mime));
        using var client = new HttpClient(handler);
        var result = await Run(new WebTool(null, client, client),
            """{"action":"fetch","url":"https://example.test/article"}""");
        Assert.Contains(expected, result);
        Assert.DoesNotContain("hidden()", result);
    }
}
