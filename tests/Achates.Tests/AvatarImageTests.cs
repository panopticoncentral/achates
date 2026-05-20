using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class AvatarImageTests
{
    [Fact]
    public void ResolvesCrossAgentUrlToOwningAgent()
    {
        var resolved = AvatarImage.TryResolveImagePath(
            "/agents/friday/images/x.jpg",
            Path.Combine("/root", "assistant"));

        Assert.Equal(Path.Combine("/root", "friday", "images", "x.jpg"), resolved);
    }

    [Fact]
    public void ResolvesSameAgentUrlUnderThatAgent()
    {
        var resolved = AvatarImage.TryResolveImagePath(
            "/agents/friday/images/x.jpg",
            Path.Combine("/root", "friday"));

        Assert.Equal(Path.Combine("/root", "friday", "images", "x.jpg"), resolved);
    }

    [Fact]
    public void ReturnsAbsolutePathUnchanged()
    {
        var resolved = AvatarImage.TryResolveImagePath("/tmp/foo.jpg", "/root/assistant");

        Assert.Equal("/tmp/foo.jpg", resolved);
    }

    [Fact]
    public void ResolvesBareFilenameUnderAgentDir()
    {
        var agentDir = Path.Combine("/root", "assistant");
        var resolved = AvatarImage.TryResolveImagePath("foo.png", agentDir);

        Assert.Equal(Path.Combine(agentDir, "images", "foo.png"), resolved);
    }

    [Theory]
    [InlineData("/agents/../images/x.jpg")]
    [InlineData("/agents/friday/images/..%2Fx.jpg")]
    [InlineData("/agents/friday/images/../../secret.jpg")]
    public void RejectsTraversalAttempts(string value)
    {
        var resolved = AvatarImage.TryResolveImagePath(value, Path.Combine("/root", "assistant"));

        Assert.Null(resolved);
    }

    [Fact]
    public void ReturnsNullForNonPathValue()
    {
        var resolved = AvatarImage.TryResolveImagePath(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAen63NgAAAAASUVORK5CYII=",
            "/root/assistant");

        Assert.Null(resolved);
    }
}
