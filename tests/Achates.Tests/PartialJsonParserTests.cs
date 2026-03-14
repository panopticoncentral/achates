using Achates.Providers.Util;

namespace Achates.Tests;

public sealed class PartialJsonParserTests
{
    // --- Complete JSON ---

    [Fact]
    public void Parses_complete_json_object()
    {
        var result = PartialJsonParser.ParseStreamingJson("""{"name": "test", "count": 42}""");

        Assert.Equal("test", result["name"]?.ToString());
    }

    [Fact]
    public void Parses_empty_object()
    {
        var result = PartialJsonParser.ParseStreamingJson("{}");

        Assert.Empty(result);
    }

    // --- Null and empty input ---

    [Fact]
    public void Null_input_returns_empty_dictionary()
    {
        var result = PartialJsonParser.ParseStreamingJson(null);

        Assert.Empty(result);
    }

    [Fact]
    public void Empty_string_returns_empty_dictionary()
    {
        var result = PartialJsonParser.ParseStreamingJson("");

        Assert.Empty(result);
    }

    [Fact]
    public void Whitespace_only_returns_empty_dictionary()
    {
        var result = PartialJsonParser.ParseStreamingJson("   ");

        Assert.Empty(result);
    }

    // --- Incomplete JSON repair ---

    [Fact]
    public void Repairs_missing_closing_brace()
    {
        var result = PartialJsonParser.ParseStreamingJson("""{"name": "test" """);

        Assert.Equal("test", result["name"]?.ToString());
    }

    [Fact]
    public void Repairs_missing_closing_bracket_and_brace()
    {
        var result = PartialJsonParser.ParseStreamingJson("""{"items": [1, 2, 3""");

        Assert.True(result.ContainsKey("items"));
    }

    [Fact]
    public void Repairs_unclosed_string_value()
    {
        var result = PartialJsonParser.ParseStreamingJson("""{"name": "partial""");

        Assert.Equal("partial", result["name"]?.ToString());
    }

    [Fact]
    public void Repairs_nested_objects()
    {
        var result = PartialJsonParser.ParseStreamingJson("""{"outer": {"inner": "val" """);

        Assert.True(result.ContainsKey("outer"));
    }

    // --- Edge cases in string handling ---

    [Fact]
    public void Handles_escaped_quotes_in_strings()
    {
        var result = PartialJsonParser.ParseStreamingJson("{\"text\": \"say \\\"hello\\\"\"}");

        Assert.Equal("say \"hello\"", result["text"]?.ToString());
    }

    [Fact]
    public void Handles_escaped_backslash_before_quote()
    {
        var result = PartialJsonParser.ParseStreamingJson("{\"path\": \"c:\\\\\"}");

        Assert.Equal("c:\\", result["path"]?.ToString());
    }

    [Fact]
    public void Braces_inside_strings_are_not_tracked()
    {
        var result = PartialJsonParser.ParseStreamingJson("""{"text": "{ not a real brace }"}""");

        Assert.Equal("{ not a real brace }", result["text"]?.ToString());
    }

    // --- Totally broken input ---

    [Fact]
    public void Garbage_input_returns_empty_dictionary()
    {
        var result = PartialJsonParser.ParseStreamingJson("not json at all");

        Assert.Empty(result);
    }

    [Fact]
    public void Just_opening_brace_returns_empty_dictionary()
    {
        var result = PartialJsonParser.ParseStreamingJson("{");

        Assert.Empty(result);
    }
}
