using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Models;
using Achates.Server.Chat;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class AgentRuntimeFactoryTests
{
    private sealed class StubProvider : IModelProvider
    {
        public string Id => "stub"; public string Name => "Stub"; public string EnvironmentKey => "S";
        public string? Key { get; set; } public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model m, CompletionContext c, CompletionOptions? o = null, CancellationToken ct = default)
            => CompletionEventStream.Create(s => { s.End(); return Task.CompletedTask; });
    }

    private static Model TestModel() => new()
    {
        Id = "test/model", Name = "Test", Provider = new StubProvider(),
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000, Input = ModelModalities.Text,
        Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
    };

    [Fact]
    public void Create_includes_universal_tools()
    {
        var memoryTool = new MemoryTool("/tmp/shared.md", "/tmp/agent.md");
        var factory = new AgentRuntimeFactory(TestModel(), universalTools: [memoryTool]);

        var runtime = factory.Create([]);

        Assert.Contains(runtime.Tools, t => t.Name == "memory");
    }

    [Fact]
    public void Create_with_no_universal_tools_has_empty_tool_list()
    {
        // Backward compat: existing test code that constructs the factory without
        // universalTools still produces a tool-less runtime.
        var factory = new AgentRuntimeFactory(TestModel());

        var runtime = factory.Create([]);

        Assert.Empty(runtime.Tools);
    }
}
