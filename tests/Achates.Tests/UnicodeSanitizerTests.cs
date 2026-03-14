using Achates.Providers.Util;

namespace Achates.Tests;

public sealed class UnicodeSanitizerTests
{
    [Fact]
    public void Null_returns_null()
    {
        Assert.Null(UnicodeSanitizer.SanitizeSurrogates(null!));
    }

    [Fact]
    public void Empty_string_returns_empty()
    {
        Assert.Equal("", UnicodeSanitizer.SanitizeSurrogates(""));
    }

    [Fact]
    public void Ascii_only_returns_same_instance()
    {
        var input = "hello world";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Same(input, result);
    }

    [Fact]
    public void Valid_surrogate_pair_preserved()
    {
        // U+1F600 (grinning face) = \uD83D\uDE00
        var input = "hi \uD83D\uDE00 there";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Same(input, result);
    }

    [Fact]
    public void Unpaired_high_surrogate_replaced()
    {
        var input = "before\uD800after";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Equal("before\uFFFDafter", result);
    }

    [Fact]
    public void Unpaired_low_surrogate_replaced()
    {
        var input = "before\uDC00after";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Equal("before\uFFFDafter", result);
    }

    [Fact]
    public void High_surrogate_at_end_of_string_replaced()
    {
        var input = "trailing\uD800";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Equal("trailing\uFFFD", result);
    }

    [Fact]
    public void Two_high_surrogates_both_replaced()
    {
        var input = "\uD800\uD801";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Equal("\uFFFD\uFFFD", result);
    }

    [Fact]
    public void Reversed_surrogate_pair_both_replaced()
    {
        // Low then high is invalid
        var input = "\uDC00\uD800";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Equal("\uFFFD\uFFFD", result);
    }

    [Fact]
    public void Mixed_valid_and_invalid_surrogates()
    {
        // Valid pair followed by unpaired high
        var input = "\uD83D\uDE00\uD800";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Equal("\uD83D\uDE00\uFFFD", result);
    }

    [Fact]
    public void Non_surrogate_unicode_passes_through()
    {
        var input = "caf\u00E9 na\u00EFve";
        var result = UnicodeSanitizer.SanitizeSurrogates(input);

        Assert.Same(input, result);
    }
}
