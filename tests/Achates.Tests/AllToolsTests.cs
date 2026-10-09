using Achates.Server;

namespace Achates.Tests;

public sealed class AllToolsTests
{
    [Fact]
    public void AllTools_excludes_universal_tools_from_picker()
    {
        // memory and cost are always-on; they must not appear in the picker
        // surfaced to the iOS agent-edit sheet via the tools.list RPC.
        var names = GatewayService.AllTools.Select(t => t.Name).ToList();

        Assert.DoesNotContain("memory", names);
        Assert.DoesNotContain("cost", names);
    }

    [Fact]
    public void AllTools_includes_opt_in_tools()
    {
        var names = GatewayService.AllTools.Select(t => t.Name).ToList();

        // Sanity: opt-in tools are still surfaced.
        Assert.Contains("notebook", names);
        Assert.Contains("status", names);
        Assert.Contains("conversations", names);
        Assert.DoesNotContain("session", names);
        Assert.DoesNotContain("sessions", names);
        Assert.Equal("Status", Assert.Single(GatewayService.AllTools, t => t.Name == "status").Label);
        Assert.Equal("Conversations", Assert.Single(GatewayService.AllTools, t => t.Name == "conversations").Label);
        Assert.Contains("web", names);
        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("web_fetch", names);
        Assert.Equal("Web", Assert.Single(GatewayService.AllTools, t => t.Name == "web").Label);
        Assert.Contains("web", GatewayService.AllToolNames);
        Assert.DoesNotContain("web_search", GatewayService.AllToolNames);
        Assert.DoesNotContain("web_fetch", GatewayService.AllToolNames);
        Assert.Contains("feed_fetch", names);
    }
}
