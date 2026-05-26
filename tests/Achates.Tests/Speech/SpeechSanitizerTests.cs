using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class SpeechSanitizerTests
{
    [Theory]
    [InlineData("**bold** word", "bold word")]
    [InlineData("_italic_ word", "italic word")]
    [InlineData("# Heading", "Heading")]
    [InlineData("plain prose", "plain prose")]
    public void Strips_inline_emphasis_and_headers(string input, string expected)
    {
        Assert.Equal(expected, SpeechSanitizer.Sanitize(input));
    }

    [Fact]
    public void Drops_entire_code_fence_block()
    {
        var input = "Before.\n```py\nprint('hi')\n```\nAfter.";
        Assert.Equal("Before.\n\nAfter.", SpeechSanitizer.Sanitize(input));
    }

    [Fact]
    public void Drops_inline_code_contents()
    {
        Assert.Equal("Use the  function.", SpeechSanitizer.Sanitize("Use the `foo.bar()` function."));
    }

    [Fact]
    public void Keeps_link_text_drops_url()
    {
        Assert.Equal("See the docs for more.", SpeechSanitizer.Sanitize("See the [docs](https://example.com) for more."));
    }

    [Fact]
    public void Strips_bare_urls()
    {
        Assert.Equal("Check out  for that.", SpeechSanitizer.Sanitize("Check out https://example.com/foo?x=1 for that."));
    }

    [Fact]
    public void Strips_image_references()
    {
        Assert.Equal("Behold: ", SpeechSanitizer.Sanitize("Behold: ![cat](https://example.com/cat.png)"));
    }

    [Fact]
    public void Strips_emoji()
    {
        Assert.Equal("Hello world", SpeechSanitizer.Sanitize("Hello 😀 world 🚀"));
    }

    [Fact]
    public void Strips_adjacent_emoji_without_double_space()
    {
        Assert.Equal("word word", SpeechSanitizer.Sanitize("word 😀😀 word"));
    }

    [Fact]
    public void Strips_emoji_at_start_and_end_of_string()
    {
        Assert.Equal("hello", SpeechSanitizer.Sanitize("🚀 hello 🎉"));
    }

    [Fact]
    public void Strips_emoji_only_string_to_empty()
    {
        Assert.Equal(string.Empty, SpeechSanitizer.Sanitize("😀🚀🎉").Trim());
    }

    [Fact]
    public void Strips_blockquote_marker_keeps_content()
    {
        Assert.Equal("a quoted line", SpeechSanitizer.Sanitize("> a quoted line"));
    }

    [Fact]
    public void Collapses_horizontal_rule_to_nothing()
    {
        Assert.Equal("Before\n\nAfter", SpeechSanitizer.Sanitize("Before\n---\nAfter"));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, SpeechSanitizer.Sanitize(""));
    }
}
