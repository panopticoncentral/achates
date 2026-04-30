using System.Text.Json;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class AttachmentParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Missing_attachments_returns_empty_list()
    {
        var p = Parse("""{"text":"hi"}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.NotNull(result);
        Assert.Empty(result!);
        Assert.Null(error);
    }

    [Fact]
    public void Empty_array_returns_empty_list()
    {
        var p = Parse("""{"attachments":[]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.NotNull(result);
        Assert.Empty(result!);
        Assert.Null(error);
    }

    [Fact]
    public void Single_jpeg_attachment_parses()
    {
        var data = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        var p = Parse($$"""{"attachments":[{"mime":"image/jpeg","data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        var single = Assert.Single(result!);
        var image = Assert.IsType<CompletionImageContent>(single);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(data, image.Data);
    }

    [Fact]
    public void Four_attachments_parse()
    {
        var data = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var p = Parse($$"""
        {"attachments":[
            {"mime":"image/jpeg","data":"{{data}}"},
            {"mime":"image/png","data":"{{data}}"},
            {"mime":"image/webp","data":"{{data}}"},
            {"mime":"image/heic","data":"{{data}}"}
        ]}
        """);
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(4, result!.Count);
    }

    [Fact]
    public void Five_attachments_return_error()
    {
        var data = Convert.ToBase64String(new byte[] { 1 });
        var item = $$"""{"mime":"image/jpeg","data":"{{data}}"}""";
        var p = Parse($$"""{"attachments":[{{item}},{{item}},{{item}},{{item}},{{item}}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("Too many", error);
    }

    [Fact]
    public void Bad_mime_returns_error()
    {
        var data = Convert.ToBase64String(new byte[] { 1 });
        var p = Parse($$"""{"attachments":[{"mime":"image/gif","data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("mime", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_base64_returns_error()
    {
        var p = Parse("""{"attachments":[{"mime":"image/jpeg","data":"not!base64@@"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void Oversize_attachment_returns_error()
    {
        // 9 MB of zero bytes encoded as base64
        var bytes = new byte[9 * 1024 * 1024];
        var data = Convert.ToBase64String(bytes);
        var p = Parse($$"""{"attachments":[{"mime":"image/jpeg","data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("too large", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_array_attachments_returns_error()
    {
        var p = Parse("""{"attachments":"oops"}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void Missing_data_field_returns_error()
    {
        var p = Parse("""{"attachments":[{"mime":"image/jpeg"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void Missing_mime_field_returns_error()
    {
        var data = Convert.ToBase64String(new byte[] { 1 });
        var p = Parse($$"""{"attachments":[{"data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void Pdf_attachment_parses_to_file_content()
    {
        var data = Convert.ToBase64String(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
        var p = Parse($$"""{"attachments":[{"mime":"application/pdf","data":"{{data}}","filename":"report.pdf"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        var single = Assert.Single(result!);
        var file = Assert.IsType<CompletionFileContent>(single);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal(data, file.Data);
        Assert.Equal("report.pdf", file.FileName);
    }

    [Fact]
    public void Pdf_without_filename_uses_default()
    {
        var data = Convert.ToBase64String(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var p = Parse($$"""{"attachments":[{"mime":"application/pdf","data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(error);
        var file = Assert.IsType<CompletionFileContent>(Assert.Single(result!));
        Assert.Equal("document.pdf", file.FileName);
    }

    [Fact]
    public void Pdf_under_32mb_parses()
    {
        // 16 MB — well under the 32 MB PDF cap, but well over the 8 MB image cap.
        var bytes = new byte[16 * 1024 * 1024];
        var data = Convert.ToBase64String(bytes);
        var p = Parse($$"""{"attachments":[{"mime":"application/pdf","data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.IsType<CompletionFileContent>(Assert.Single(result!));
    }

    [Fact]
    public void Pdf_over_32mb_returns_error()
    {
        var bytes = new byte[33 * 1024 * 1024];
        var data = Convert.ToBase64String(bytes);
        var p = Parse($$"""{"attachments":[{"mime":"application/pdf","data":"{{data}}"}]}""");
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("too large", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mixed_image_and_pdf_attachments_parse()
    {
        var imgData = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        var pdfData = Convert.ToBase64String(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var p = Parse($$"""
        {"attachments":[
            {"mime":"image/jpeg","data":"{{imgData}}"},
            {"mime":"application/pdf","data":"{{pdfData}}","filename":"a.pdf"}
        ]}
        """);
        var result = AttachmentParser.Parse(p, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.IsType<CompletionImageContent>(result[0]);
        Assert.IsType<CompletionFileContent>(result[1]);
    }
}
