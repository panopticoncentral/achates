using Achates.Transports;

namespace Achates.Tests;

public sealed class TelegramHtmlRendererTests
{
    // --- Inline formatting ---

    [Fact]
    public void Bold_text()
    {
        var result = TelegramHtmlRenderer.Convert("**hello**");
        Assert.Equal("<b>hello</b>", result);
    }

    [Fact]
    public void Italic_text()
    {
        var result = TelegramHtmlRenderer.Convert("*hello*");
        Assert.Equal("<i>hello</i>", result);
    }

    [Fact]
    public void Strikethrough_text()
    {
        var result = TelegramHtmlRenderer.Convert("~~hello~~");
        Assert.Equal("<s>hello</s>", result);
    }

    [Fact]
    public void Inline_code()
    {
        var result = TelegramHtmlRenderer.Convert("`code`");
        Assert.Equal("<code>code</code>", result);
    }

    [Fact]
    public void Nested_bold_and_italic()
    {
        var result = TelegramHtmlRenderer.Convert("***bold and italic***");
        Assert.Equal("<i><b>bold and italic</b></i>", result);
    }

    // --- Links ---

    [Fact]
    public void Link()
    {
        var result = TelegramHtmlRenderer.Convert("[click](https://example.com)");
        Assert.Equal("<a href=\"https://example.com\">click</a>", result);
    }

    [Fact]
    public void Image_renders_as_link()
    {
        var result = TelegramHtmlRenderer.Convert("![alt](https://example.com/img.png)");
        Assert.Equal("<a href=\"https://example.com/img.png\">alt</a>", result);
    }

    // --- Headings ---

    [Fact]
    public void Heading_renders_as_bold()
    {
        var result = TelegramHtmlRenderer.Convert("# Title");
        Assert.Equal("<b>Title</b>", result);
    }

    [Fact]
    public void H2_renders_as_bold()
    {
        var result = TelegramHtmlRenderer.Convert("## Subtitle");
        Assert.Equal("<b>Subtitle</b>", result);
    }

    // --- Code blocks ---

    [Fact]
    public void Fenced_code_block()
    {
        var result = TelegramHtmlRenderer.Convert("```\nvar x = 1;\n```");
        Assert.Equal("<pre><code>var x = 1;</code></pre>", result);
    }

    [Fact]
    public void Fenced_code_block_with_language()
    {
        var result = TelegramHtmlRenderer.Convert("```csharp\nvar x = 1;\n```");
        Assert.Equal("<pre><code class=\"language-csharp\">var x = 1;</code></pre>", result);
    }

    [Fact]
    public void Code_block_escapes_html()
    {
        var result = TelegramHtmlRenderer.Convert("```\n<div>test</div>\n```");
        Assert.Equal("<pre><code>&lt;div&gt;test&lt;/div&gt;</code></pre>", result);
    }

    // --- Lists ---

    [Fact]
    public void Unordered_list()
    {
        var result = TelegramHtmlRenderer.Convert("- one\n- two\n- three");
        Assert.Equal("• one\n• two\n• three", result);
    }

    [Fact]
    public void Ordered_list()
    {
        var result = TelegramHtmlRenderer.Convert("1. one\n2. two\n3. three");
        Assert.Equal("1. one\n2. two\n3. three", result);
    }

    [Fact]
    public void Nested_list()
    {
        var result = TelegramHtmlRenderer.Convert("- outer\n  - inner");
        Assert.Equal("• outer\n\n  • inner", result);
    }

    [Fact]
    public void Task_list()
    {
        var result = TelegramHtmlRenderer.Convert("- [x] done\n- [ ] todo");
        Assert.Equal("☑  done\n☐  todo", result);
    }

    // --- Blockquotes ---

    [Fact]
    public void Blockquote()
    {
        var result = TelegramHtmlRenderer.Convert("> quoted text");
        Assert.Equal("<blockquote>quoted text\n</blockquote>", result);
    }

    // --- Thematic break ---

    [Fact]
    public void Thematic_break()
    {
        var result = TelegramHtmlRenderer.Convert("above\n\n---\n\nbelow");
        Assert.Equal("above\n\n---\n\nbelow", result);
    }

    // --- HTML escaping ---

    [Fact]
    public void Escapes_html_entities_in_text()
    {
        var result = TelegramHtmlRenderer.Convert("1 < 2 & 3 > 0");
        Assert.Equal("1 &lt; 2 &amp; 3 &gt; 0", result);
    }

    // --- Tables ---

    [Fact]
    public void Simple_table()
    {
        var md = "| Name | Score |\n|------|-------|\n| Alice | 95 |\n| Bob | 87 |";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("<pre>", result);
        Assert.Contains("</pre>", result);
        Assert.Contains("Alice", result);
        Assert.Contains("Bob", result);
        Assert.Contains("│", result);
        Assert.Contains("─", result);
    }

    [Fact]
    public void Table_columns_are_aligned()
    {
        var md = "| Name | Score |\n|------|-------|\n| Alice | 95 |\n| Bob | 87 |";
        var result = TelegramHtmlRenderer.Convert(md);

        var lines = result.Split('\n');
        // Data rows should have the same length (monospace alignment)
        var aliceLine = lines.First(l => l.Contains("Alice"));
        var bobLine = lines.First(l => l.Contains("Bob"));
        Assert.Equal(aliceLine.Length, bobLine.Length);
    }

    [Fact]
    public void Table_with_missing_trailing_cells_does_not_crash()
    {
        // Regression test: rows with fewer cells than columns should not NRE
        var md = "| A | B | C |\n|---|---|---|\n| 1 |";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("<pre>", result);
        Assert.Contains("1", result);
    }

    [Fact]
    public void Table_escapes_html_entities_in_cells()
    {
        var md = "| Col |\n|-----|\n| A & B |";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("A &amp; B", result);
    }

    // --- Mixed content ---

    [Fact]
    public void Multiple_blocks_separated_by_blank_lines()
    {
        var md = "# Title\n\nSome text.\n\n- item";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("<b>Title</b>", result);
        Assert.Contains("Some text.", result);
        Assert.Contains("• item", result);
    }

    [Fact]
    public void Bold_inside_list_item()
    {
        var result = TelegramHtmlRenderer.Convert("- **important** item");
        Assert.Equal("• <b>important</b> item", result);
    }

    // --- Edge cases ---

    [Fact]
    public void Empty_string_returns_empty()
    {
        var result = TelegramHtmlRenderer.Convert("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Whitespace_only_returns_empty()
    {
        var result = TelegramHtmlRenderer.Convert("   \n  \n   ");
        Assert.Equal("", result);
    }

    [Fact]
    public void Multiple_paragraphs_separated_by_blank_line()
    {
        var result = TelegramHtmlRenderer.Convert("First paragraph.\n\nSecond paragraph.");
        Assert.Equal("First paragraph.\n\nSecond paragraph.", result);
    }

    // --- Autolinks and line breaks ---

    [Fact]
    public void Autolink()
    {
        var result = TelegramHtmlRenderer.Convert("<https://example.com>");
        Assert.Equal("<a href=\"https://example.com\">https://example.com</a>", result);
    }

    [Fact]
    public void Hard_line_break_with_backslash()
    {
        var result = TelegramHtmlRenderer.Convert("line one\\\nline two");
        Assert.Equal("line one\nline two", result);
    }

    // --- HTML entities ---

    [Fact]
    public void Html_entity_in_source()
    {
        var result = TelegramHtmlRenderer.Convert("&copy; 2025");
        Assert.Contains("&#169; 2025", result); // entity decoded then re-encoded by Escape()
    }

    // --- Additional table edge cases ---

    [Fact]
    public void Table_with_single_column()
    {
        var md = "| Name |\n|------|\n| Alice |";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("<pre>", result);
        Assert.Contains("Alice", result);
        Assert.Contains("─", result);
    }

    [Fact]
    public void Table_with_inline_code_in_cell()
    {
        var md = "| Command |\n|---------|\n| `ls -la` |";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("ls -la", result);
    }

    [Fact]
    public void Table_with_empty_cells()
    {
        var md = "| A | B |\n|---|---|\n|   | x |";
        var result = TelegramHtmlRenderer.Convert(md);

        Assert.Contains("<pre>", result);
        Assert.Contains("x", result);
    }
}
