using System.Net;
using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Providers.Models;
using Achates.Server.Graph;
using Achates.Server.Tools;
using Achates.Server.Workbooks;

namespace Achates.Tests;

public sealed class MailToolTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private static GraphClient Client(Handler handler) => new(new HttpClient(handler), _ => Task.FromResult("test-token"));
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"),
    };
    private static object Metadata(string name = "report.pdf", string mime = "application/pdf",
        long size = 6, string type = "#microsoft.graph.fileAttachment") => new Dictionary<string, object?>
        {
            ["@odata.type"] = type, ["id"] = "att/+?", ["name"] = name,
            ["contentType"] = mime, ["size"] = size, ["isInline"] = true,
        };
    private static Dictionary<string, object?> Args(string action = "read_attachment") => new()
    {
        ["action"] = action, ["message_id"] = "msg/+?", ["attachment_id"] = "att/+?",
    };
    private static string Text(AgentToolResult result) => string.Join("\n", result.Content.OfType<CompletionTextContent>().Select(c => c.Text));
    private static MailTool Tool(Handler handler, ModelModalities input = ModelModalities.File | ModelModalities.Image) =>
        new(new Dictionary<string, GraphClient> { ["personal"] = Client(handler) }, input);

    [Fact]
    public async Task Read_discovers_inline_attachments_without_downloading_content()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/attachments")
            ? Json(new { value = new[] { Metadata() } })
            : Json(new { subject = "Synthetic message", body = new { content = "Body text" }, hasAttachments = false }));
        var tool = Tool(handler);
        var result = await tool.ExecuteAsync("t", Args("read"));
        Assert.Contains("Body text", Text(result));
        Assert.Contains("report.pdf", Text(result));
        Assert.Contains("att/+?", Text(result));
        Assert.Contains("read_attachment", Text(result));
        Assert.Contains("inline", Text(result));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, url => Assert.DoesNotContain("contentBytes", url));
        Assert.Contains("msg%2F%2B%3F", handler.Requests[0]);
    }

    [Fact]
    public async Task Pdf_uses_injected_content_with_exact_bytes_and_escaped_ids()
    {
        var bytes = Encoding.UTF8.GetBytes("%PDF-synthetic");
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/$value")
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : Json(Metadata()));
        var result = await Tool(handler).ExecuteAsync("t", Args());
        var pdf = Assert.IsType<CompletionFileContent>(Assert.Single(result.InjectedUserContent!));
        Assert.Equal(bytes, Convert.FromBase64String(pdf.Data));
        Assert.Equal("report.pdf", pdf.FileName);
        Assert.All(result.Content, block => Assert.IsType<CompletionTextContent>(block));
        Assert.Contains("msg%2F%2B%3F/attachments/att%2F%2B%3F/$value", handler.Requests[1]);
    }

    [Theory]
    [InlineData("report.pdf", "application/pdf", "can't read PDF")]
    [InlineData("image.png", "image/png", "can't read images")]
    [InlineData("archive.zip", "application/zip", "Unsupported attachment type")]
    [InlineData("book.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "interactive conversation")]
    public async Task Unsupported_inputs_are_rejected_before_downloading(string name, string mime, string expected)
    {
        using var handler = new Handler(_ => Json(Metadata(name, mime)));
        var result = await Tool(handler, ModelModalities.Text).ExecuteAsync("t", Args());
        Assert.Contains(expected, Text(result));
        Assert.Null(result.InjectedUserContent);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("#microsoft.graph.itemAttachment")]
    [InlineData("#microsoft.graph.referenceAttachment")]
    public async Task Non_file_attachments_do_not_attempt_raw_downloads(string type)
    {
        using var handler = new Handler(_ => Json(Metadata(type: type)));
        var result = await Tool(handler).ExecuteAsync("t", Args());
        Assert.Contains("Only file attachments are supported", Text(result));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Text_uses_existing_text_parser_and_generic_mime_filename_fallback()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/$value")
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("column,value\nexample,12")) }
            : Json(Metadata("data.csv", "application/octet-stream")));
        var result = await Tool(handler, ModelModalities.Text).ExecuteAsync("t", Args());
        Assert.Contains("data.csv", Text(result));
        Assert.Contains("example,12", Text(result));
        Assert.Null(result.InjectedUserContent);
    }

    [Fact]
    public async Task Image_is_injected_with_normalized_mime()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/$value")
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
            : Json(Metadata("image.png", "IMAGE/PNG; name=image.png")));
        var result = await Tool(handler).ExecuteAsync("t", Args());
        var image = Assert.IsType<CompletionImageContent>(Assert.Single(result.InjectedUserContent!));
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public async Task Workbook_original_is_archived_and_available_to_range_tool_after_reopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "achates-mail-test-" + Guid.NewGuid().ToString("N"));
        var bytes = WorkbookTests.CreateWorkbook();
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/$value")
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : Json(Metadata("sample.xlsx", CompletionWorkbookContent.ExcelMime, bytes.Length)));
        try
        {
            var result = await Tool(handler).ForSession(new WorkbookStore(directory)).ExecuteAsync("t", Args());
            var workbook = Assert.IsType<CompletionWorkbookContent>(Assert.Single(result.InjectedUserContent!));
            var reopened = new WorkbookStore(directory);
            Assert.Equal(bytes, await reopened.ReadAsync(workbook.WorkbookId, default));
            var range = await new WorkbookTool(reopened).ExecuteAsync("w", new()
            {
                ["action"] = "read", ["workbook_id"] = workbook.WorkbookId, ["sheet"] = "Sales", ["range"] = "A1:C2",
            });
            Assert.Contains("Sample", Text(range));
            Assert.Contains("99", Text(range));
            Assert.Contains("B2*2", Text(range));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Metadata_size_limit_prevents_download()
    {
        using var handler = new Handler(_ => Json(Metadata(size: 33 * 1024 * 1024)));
        var result = await Tool(handler).ExecuteAsync("t", Args());
        Assert.Contains("too large", Text(result));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_limit_is_enforced_even_without_content_length(bool knownLength)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(new byte[1025]) : new UnknownLengthContent(new byte[1025]),
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).GetBytesAsync("messages/m/attachments/a/$value", 1024, default));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    [Fact]
    public async Task Mail_body_survives_attachment_listing_failure_with_retry_guidance()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/attachments")
            ? new(HttpStatusCode.Forbidden) { Content = new StringContent("Access denied") }
            : Json(new { body = new { content = "Visible body" } }));
        var result = await Tool(handler).ExecuteAsync("t", Args("read"));
        Assert.Contains("Visible body", Text(result));
        Assert.Contains("403", Text(result));
        Assert.Contains("Retry with action=attachments", Text(result));
    }

    [Fact]
    public async Task Explicit_account_is_used_and_unknown_account_never_falls_back()
    {
        using var personal = new Handler(_ => throw new Exception("Wrong account"));
        using var work = new Handler(_ => Json(new { value = new[] { Metadata() } }));
        var tool = new MailTool(new Dictionary<string, GraphClient> { ["personal"] = Client(personal), ["work"] = Client(work) });
        var args = Args("attachments");
        args["account"] = "work";
        await tool.ExecuteAsync("t", args);
        Assert.Single(work.Requests);
        args["account"] = "missing";
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync("t", args));
        Assert.Empty(personal.Requests);
    }

    [Fact]
    public async Task Attachment_pages_expose_continuation_without_loading_bytes()
    {
        using var handler = new Handler(_ => Json(new Dictionary<string, object?>
        {
            ["value"] = new[] { Metadata() }, ["@odata.nextLink"] = "next-page",
        }));
        var args = Args("attachments");
        args["offset"] = 50;
        var result = await Tool(handler).ExecuteAsync("t", args);
        Assert.Contains("offset=51", Text(result));
        Assert.Contains("$skip=50", handler.Requests.Single());
    }

    [Fact]
    public async Task Missing_ids_do_not_make_network_requests()
    {
        using var handler = new Handler(_ => throw new Exception("Unexpected request"));
        var result = await Tool(handler).ExecuteAsync("t", new() { ["action"] = "read_attachment" });
        Assert.Contains("required", Text(result));
        Assert.Empty(handler.Requests);
    }
}
