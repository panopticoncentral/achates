using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;

namespace Achates.Tests;

/// <summary>
/// Tool names are the identifier the model uses to call a tool, and providers reject a
/// request whose tool list contains two tools with the same name. Several tool-building
/// paths can legitimately produce an overlap — e.g. resuming a dreamtime session re-injects
/// a <c>sessions</c> tool that an agent already lists explicitly. The runtime must dedupe
/// defensively so no caller can ever ship duplicate names to the provider.
/// </summary>
public class AgentRuntimeToolDedupeTests
{
    private sealed class NamedTool(string name) : AgentTool
    {
        public override string Name => name;
        public override string Description => $"Tool {name}.";
        public override JsonElement Parameters =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            Func<AgentToolResult, Task>? onProgress = null) =>
            Task.FromResult(new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "ok" }],
            });
    }

    [Fact]
    public void Constructor_dedupes_tools_with_the_same_name()
    {
        var runtime = new AgentRuntime(new AgentOptions
        {
            Tools = [new NamedTool("sessions"), new NamedTool("memory"), new NamedTool("sessions")],
        });

        Assert.Equal(2, runtime.Tools.Count);
        Assert.Single(runtime.Tools, t => t.Name == "sessions");
        Assert.Single(runtime.Tools, t => t.Name == "memory");
    }

    [Fact]
    public void Constructor_keeps_the_last_tool_for_a_duplicated_name()
    {
        var first = new NamedTool("sessions");
        var last = new NamedTool("sessions");

        var runtime = new AgentRuntime(new AgentOptions
        {
            Tools = [first, last],
        });

        Assert.Same(last, Assert.Single(runtime.Tools));
    }

    [Fact]
    public void SetTools_dedupes_tools_with_the_same_name()
    {
        var runtime = new AgentRuntime();

        runtime.SetTools([new NamedTool("sessions"), new NamedTool("sessions")]);

        Assert.Single(runtime.Tools);
    }
}
