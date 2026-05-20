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
        Assert.Contains("web_search", names);
    }
}
