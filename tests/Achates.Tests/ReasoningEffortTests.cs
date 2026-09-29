using System.Net;
using System.Text;
using System.Text.Json;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Providers.OpenRouter;
using Achates.Server;
using Achates.Server.Mobile;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class ReasoningEffortTests
{
    [Theory]
    [InlineData("low", "high")]
    [InlineData("default", "default")]
    public void Efforts_survive_editor_update_and_markdown_roundtrip(string regular, string thinking)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            reasoning_effort = regular, thinking_reasoning_effort = thinking,
        });
        var config = MobileTransport.BuildUpdatedAgentConfig(payload, null);
        var restored = AgentLoader.Parse(AgentLoader.Serialize("Test", config))!;
        Assert.Equal(regular, restored.Completion?.ReasoningEffort);
        Assert.Equal(thinking, restored.ThinkingReasoningEffort);
    }

    [Theory]
    [InlineData(false, null, "default")]
    [InlineData(true, null, "medium")]
    [InlineData(true, "low", "low")]
    [InlineData(true, "default", "default")]
    public void Older_clients_preserve_effective_efforts_when_omitted(
        bool hasCompletion, string? effort, string expected)
    {
        var existing = new AgentConfig
        {
            Completion = hasCompletion ? new CompletionConfig { ReasoningEffort = effort } : null,
            ThinkingReasoningEffort = "high",
        };
        var updated = MobileTransport.BuildUpdatedAgentConfig(
            JsonSerializer.SerializeToElement(new { temperature = 0.5 }), existing);
        var restored = AgentLoader.Parse(AgentLoader.Serialize("Test", updated))!;
        Assert.Equal(expected, ReasoningEffortSettings.Regular(restored.Completion));
        Assert.Equal("high", restored.ThinkingReasoningEffort);
        Assert.Equal(effort, existing.Completion?.ReasoningEffort);
    }

    [Fact]
    public void Legacy_regular_defaults_and_absent_thinking_effort_are_preserved()
    {
        using var client = new HttpClient(new CaptureHandler());
        var model = MakeModel(client, supportsEffort: true);
        Assert.Null(GatewayService.BuildCompletionOptions(null, model));
        Assert.Equal("medium", GatewayService.BuildCompletionOptions(new CompletionConfig(), model)?.ReasoningEffort);
        var config = AgentLoader.Parse("# Test\n\n## Capabilities\n\n**Reasoning Effort:** low\n")!;
        Assert.Null(config.ThinkingReasoningEffort);
        Assert.Equal("low", GatewayService.BuildCompletionOptions(config.Completion, model)?.ReasoningEffort);
    }

    [Theory]
    [InlineData("low", "high", true, "low", "high")]
    [InlineData("high", "low", true, "high", "low")]
    [InlineData("high", null, true, "high", null)]
    [InlineData("default", "default", true, null, null)]
    [InlineData("low", "high", false, null, null)]
    public async Task Provider_requests_use_independent_efforts_and_omit_defaults_or_unsupported_values(
        string regular, string? thinking, bool supported, string? expectedRegular, string? expectedThinking)
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/api/v1/") };
        var model = MakeModel(client, supported);
        var options = GatewayService.BuildCompletionOptions(new CompletionConfig
        {
            ReasoningEffort = regular, Temperature = 0.25, MaxTokens = 200,
        }, model);
        var stream = model.Provider.GetCompletions(model,
            new CompletionContext { Messages = [new CompletionUserTextMessage { Text = "Test" }] }, options);
        await foreach (var _ in stream) { }
        Assert.Null((await stream.ResultAsync).ErrorMessage);

        var think = new ThinkTool(model, "test", reasoningEffort: thinking);
        await think.ExecuteAsync("think-1", new() { ["prompt"] = "Think carefully" });

        Assert.Equal(2, handler.Requests.Count);
        AssertEffort(handler.Requests[0], expectedRegular);
        AssertEffort(handler.Requests[1], expectedThinking);
        Assert.False(handler.Requests[1].TryGetProperty("temperature", out _));
        Assert.False(handler.Requests[1].TryGetProperty("max_tokens", out _));
    }

    private static void AssertEffort(JsonElement request, string? expected)
    {
        if (expected is null)
            Assert.False(request.TryGetProperty("reasoning", out _));
        else
            Assert.Equal(expected, request.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    private static Model MakeModel(HttpClient client, bool supportsEffort) => new()
    {
        Id = "test-model", Name = "Test",
        Provider = new OpenRouterProvider { HttpClient = client, Key = "test-key" },
        Cost = new ModelCost { Prompt = 0, Completion = 0 }, ContextWindow = 8000,
        Input = ModelModalities.Text, Output = ModelModalities.Text,
        Parameters = ModelParameters.Temperature | ModelParameters.MaxTokens | ModelParameters.Reasoning
            | (supportsEffort ? ModelParameters.ReasoningEffort : 0),
    };

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(json.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Done\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
