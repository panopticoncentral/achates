using Achates.Server;

namespace Achates.Tests;

public sealed class AgentLoaderTests
{
    [Theory]
    [InlineData("Paul", "paul")]
    [InlineData("Paul's Assistant", "pauls-assistant")]
    [InlineData("My Agent 2", "my-agent-2")]
    [InlineData("Valerie", "valerie")]
    [InlineData("  Spaces  ", "spaces")]
    [InlineData("--dashes--", "dashes")]
    [InlineData("a--b", "a-b")]
    [InlineData("UPPER CASE", "upper-case")]
    [InlineData("hello world!", "hello-world")]
    [InlineData("René", "ren")]
    [InlineData("", null)]
    [InlineData("!!!", null)]
    public void NormalizeId_ProducesExpectedResult(string input, string? expected)
    {
        var result = AgentLoader.NormalizeId(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeId_TruncatesAt64Characters()
    {
        var longName = new string('a', 100);
        var result = AgentLoader.NormalizeId(longName);
        Assert.NotNull(result);
        Assert.Equal(64, result!.Length);
    }
}
