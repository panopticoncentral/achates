using System.Net;
using System.Text;
using System.Text.Json;
using Achates.Providers.Completions.Content;
using Achates.Server;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class FeedFetchToolTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel><title>Example feed</title><link>https://example.test/</link><description>News</description>
            <item><guid isPermaLink="false">one</guid><title>First</title><link>https://example.test/one</link>
              <pubDate>Thu, 01 Oct 2026 10:00:00 -0700</pubDate><author>author@example.test</author><category>Software</category>
              <description><![CDATA[<p>Hello &amp; goodbye.</p><script>bad()</script><p>Next.</p>]]></description>
              <content:encoded><![CDATA[<h1>Full article</h1><p>More text.</p>]]></content:encoded>
            </item>
            <item><guid isPermaLink="false">two</guid><title>Second</title><pubDate>Fri, 02 Oct 2026 00:00:00 GMT</pubDate></item>
            <item><title>Undated</title><link>javascript:alert(1)</link></item>
          </channel>
        </rss>
        """;

    private const string Atom = """
        <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.test/blog/">
          <title>Atom example</title><id>urn:example:feed</id><updated>2026-10-03T00:00:00Z</updated>
          <link rel="self" href="feed.xml"/><link rel="alternate" href="./"/>
          <entry xml:base="posts/">
            <id>urn:example:entry</id><title type="html">Hello &amp;amp; world</title>
            <link rel="enclosure" href="audio.mp3"/><link rel="alternate" href="one"/>
            <published>2026-10-01T02:00:00+02:00</published><updated>2026-10-02T00:00:00Z</updated>
            <author><name>Alice</name></author><category term="Releases"/>
            <summary type="text">A &lt; B</summary>
            <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>One</p><p>Two</p><style>hidden</style></div></content>
          </entry>
        </feed>
        """;

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return send(request, cancellationToken);
        }
    }

    private static Dictionary<string, object?> Args() => new() { ["url"] = "https://example.test/feed" };
    private static Handler Respond(string xml, string mime = "application/rss+xml") => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, mime) }));

    private static async Task<JsonElement> Fetch(Handler handler, Dictionary<string, object?>? args = null, CancellationToken token = default)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        var result = await new FeedFetchTool(client).ExecuteAsync("call", args ?? Args(), token);
        var text = Assert.IsType<CompletionTextContent>(Assert.Single(result.Content)).Text;
        Assert.True(text.Length <= FeedFetchTool.MaxOutputChars);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Rss_returns_normalized_fields_and_plain_text_in_feed_order()
    {
        using var handler = Respond(Rss);
        var result = await Fetch(handler);
        Assert.Equal("Example feed", result.GetProperty("feed").GetProperty("title").GetString());
        Assert.Equal(3, result.GetProperty("returned_items").GetInt32());
        var items = result.GetProperty("items");
        Assert.Equal("one", items[0].GetProperty("id").GetString());
        Assert.Equal("Hello & goodbye. Next.", items[0].GetProperty("summary").GetString());
        Assert.Equal("2026-10-01T17:00:00+00:00", items[0].GetProperty("published_at").GetString());
        Assert.Equal("author@example.test", items[0].GetProperty("authors")[0].GetString());
        Assert.Equal("Software", items[0].GetProperty("categories")[0].GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("published_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("url").ValueKind);
        Assert.Contains("untrusted", result.GetProperty("content_notice").GetString());
        Assert.False(result.GetProperty("items_truncated").GetBoolean());
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Rss_embedded_content_is_opt_in_and_does_not_fetch_articles()
    {
        using var handler = Respond(Rss);
        var args = Args();
        args["include_content"] = true;
        var result = await Fetch(handler, args);
        Assert.Equal("Full article More text.", result.GetProperty("items")[0].GetProperty("content").GetString());
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Atom_resolves_xml_base_and_alternate_links_and_handles_xhtml()
    {
        using var handler = Respond(Atom, "application/atom+xml");
        var args = Args(); args["include_content"] = JsonSerializer.SerializeToElement(true);
        var result = await Fetch(handler, args);
        Assert.Equal("https://example.test/blog/", result.GetProperty("feed").GetProperty("website_url").GetString());
        var item = result.GetProperty("items")[0];
        Assert.Equal("https://example.test/blog/posts/one", item.GetProperty("url").GetString());
        Assert.Equal("Hello & world", item.GetProperty("title").GetString());
        Assert.Equal("A < B", item.GetProperty("summary").GetString());
        Assert.Equal("One Two", item.GetProperty("content").GetString());
        Assert.Equal("Alice", item.GetProperty("authors")[0].GetString());
        Assert.Equal("Releases", item.GetProperty("categories")[0].GetString());
        Assert.Equal("2026-10-01T00:00:00+00:00", item.GetProperty("published_at").GetString());
        Assert.Equal("2026-10-02T00:00:00+00:00", item.GetProperty("updated_at").GetString());
    }

    [Fact]
    public async Task Since_is_strict_publication_filter_and_counts_undated_entries()
    {
        using var handler = Respond(Rss);
        var args = Args(); args["since"] = "2026-10-01T17:00:00Z";
        var result = await Fetch(handler, args);
        Assert.Equal(3, result.GetProperty("total_items").GetInt32());
        Assert.Equal(1, result.GetProperty("matched_items").GetInt32());
        Assert.Equal(1, result.GetProperty("undated_items_skipped").GetInt32());
        Assert.Equal("two", result.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Invalid_dates_are_null_and_are_skipped_when_filtering()
    {
        using var handler = Respond(Rss.Replace("Thu, 01 Oct 2026 10:00:00 -0700", "not a date"));
        var result = await Fetch(handler);
        Assert.False(result.TryGetProperty("error", out _), result.ToString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("items")[0].GetProperty("published_at").ValueKind);
        var args = Args(); args["since"] = "2026-09-01T00:00:00Z";
        result = await Fetch(handler, args);
        Assert.Equal(2, result.GetProperty("undated_items_skipped").GetInt32());
    }

    [Fact]
    public async Task Limit_preserves_feed_order_and_reports_omitted_items()
    {
        using var handler = Respond(Rss);
        var args = Args(); args["limit"] = JsonSerializer.SerializeToElement(1);
        var result = await Fetch(handler, args);
        Assert.Equal(3, result.GetProperty("matched_items").GetInt32());
        Assert.Equal(1, result.GetProperty("returned_items").GetInt32());
        Assert.Equal("one", result.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.True(result.GetProperty("items_truncated").GetBoolean());
    }

    [Theory]
    [InlineData("url", "file:///tmp/feed", "invalid_url")]
    [InlineData("url", "https://user:secret@example.test/feed", "invalid_url")]
    [InlineData("url", "relative.xml", "invalid_url")]
    [InlineData("since", "2026-10-01", "invalid_since")]
    [InlineData("since", "2026-10-01T00:00:00", "invalid_since")]
    [InlineData("limit", "20", "invalid_limit")]
    [InlineData("include_content", "yes", "invalid_include_content")]
    public async Task Invalid_arguments_fail_before_network(string key, string value, string code)
    {
        using var handler = Respond(Rss);
        var args = Args(); args[key] = value;
        var result = await Fetch(handler, args);
        Assert.Equal(code, result.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(1.5)]
    [InlineData(1e30)]
    public async Task Invalid_numeric_limits_are_rejected(double limit)
    {
        using var handler = Respond(Rss);
        var args = Args(); args["limit"] = limit;
        Assert.Equal("invalid_limit", (await Fetch(handler, args)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("<html><body>Not a feed</body></html>")]
    [InlineData("<rss version='2.0'><channel><title>Incomplete")]
    [InlineData("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss version='2.0'><channel><title>&x;</title></channel></rss>")]
    [InlineData("<rss version='2.0'><channel/></rss><extra/>")]
    public async Task Unsafe_or_malformed_xml_is_rejected(string xml)
    {
        using var handler = Respond(xml);
        Assert.Equal("invalid_feed", (await Fetch(handler)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Empty_feed_and_mislabelled_content_type_are_supported()
    {
        using var handler = Respond("<rss version='2.0'><channel><title>Empty</title><link>https://example.test</link><description/></channel></rss>", "text/plain");
        var result = await Fetch(handler);
        Assert.Equal(0, result.GetProperty("returned_items").GetInt32());
        Assert.False(result.GetProperty("items_truncated").GetBoolean());
    }

    [Fact]
    public async Task Final_response_url_resolves_relative_article_links()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/redirected/feed.xml"),
            Content = new StringContent("<rss version='2.0'><channel><title>Feed</title><item><title>Item</title><link>article</link></item></channel></rss>"),
        }));
        var result = await Fetch(handler);
        Assert.Equal("https://example.test/redirected/feed.xml", result.GetProperty("feed").GetProperty("feed_url").GetString());
        Assert.Equal("https://example.test/redirected/article", result.GetProperty("items")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Output_limits_bound_text_and_whole_json()
    {
        var entry = $"<item><title>{new string('t', 1000)}</title><description>{new string('x', 10_000)}</description></item>";
        using var handler = Respond($"<rss version='2.0'><channel><title>Large</title>{string.Concat(Enumerable.Repeat(entry, 50))}</channel></rss>");
        var args = Args(); args["limit"] = 50;
        var result = await Fetch(handler, args);
        Assert.Equal(50, result.GetProperty("matched_items").GetInt32());
        Assert.True(result.GetProperty("items")[0].GetProperty("summary").ValueKind == JsonValueKind.String, result.GetProperty("items")[0].ToString());
        Assert.InRange(result.GetProperty("returned_items").GetInt32(), 1, 49);
        Assert.True(result.GetProperty("items_truncated").GetBoolean());
        foreach (var item in result.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(2000, item.GetProperty("summary").GetString()!.Length);
            Assert.Equal(512, item.GetProperty("title").GetString()!.Length);
            Assert.True(item.GetProperty("text_truncated").GetBoolean());
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task Large_unicode_entry_remains_available_with_truncation_flags()
    {
        var text = new string('界', 10_000);
        var authors = string.Concat(Enumerable.Repeat($"<author>{text}</author>", 10));
        var categories = string.Concat(Enumerable.Repeat($"<category>{text}</category>", 20));
        using var handler = Respond($"<rss version='2.0'><channel><title>{text}</title><description>{text}</description><item><title>{text}</title>{authors}{categories}<description>{text}</description></item></channel></rss>");
        var args = Args(); args["include_content"] = true;
        var result = await Fetch(handler, args);
        Assert.Equal(1, result.GetProperty("returned_items").GetInt32());
        Assert.True(result.GetProperty("feed").GetProperty("text_truncated").GetBoolean());
        Assert.True(result.GetProperty("items")[0].GetProperty("text_truncated").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_limit_counts_bytes_with_and_without_content_length(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('é', FeedFetchTool.MaxResponseBytes / 2 + 1));
        using var stream = new UnseekableStream(bytes);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(stream),
        }));
        var result = await Fetch(handler);
        Assert.Equal("feed_too_large", result.GetProperty("error").GetProperty("code").GetString());
        if (!knownLength) Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Http_and_network_errors_are_structured()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal("http_error", (await Fetch(handler)).GetProperty("error").GetProperty("code").GetString());
        using var failed = new Handler((_, _) => throw new HttpRequestException("offline"));
        Assert.Equal("request_failed", (await Fetch(failed)).GetProperty("error").GetProperty("code").GetString());
        using var timedOut = new Handler((_, _) => throw new TaskCanceledException());
        Assert.Equal("timeout", (await Fetch(timedOut)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        using var handler = new Handler((_, token) => { token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetch(handler, token: cts.Token));
    }

    [Fact]
    public void Feed_guidance_is_present_only_when_enabled()
    {
        using var client = new HttpClient();
        Assert.Contains("## Feeds", SystemPrompt.Build(tools: [new FeedFetchTool(client)]));
        Assert.DoesNotContain("## Feeds", SystemPrompt.Build());
    }
}
