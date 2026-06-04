using System.Net;
using System.Text;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Providers.OpenRouter;

namespace Achates.Tests;

/// <summary>
/// Verifies <see cref="OpenRouterProvider"/> maps OpenRouter's usage accounting onto
/// <see cref="CompletionUsage"/>. OpenRouter reports cache reads as
/// <c>prompt_tokens_details.cached_tokens</c> and cache writes as
/// <c>prompt_tokens_details.cache_write_tokens</c>, both subsets of <c>prompt_tokens</c>,
/// so the uncached input is <c>prompt_tokens - cached - cache_write</c> and each cache
/// category is billed at its own rate.
/// </summary>
public class OpenRouterUsageMappingTests
{
    private sealed class FixedHttpHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
    }

    private static async Task<CompletionAssistantMessage> RunWithUsageAsync(string usageJson)
    {
        var body =
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[],\"usage\":" + usageJson + "}\n\n"
            + "data: [DONE]\n\n";

        var provider = new OpenRouterProvider
        {
            HttpClient = new HttpClient(new FixedHttpHandler(body)) { BaseAddress = new Uri("https://example/api/v1/") },
            Key = "test-key",
        };
        var model = new Model
        {
            Id = "anthropic/claude-sonnet-4.6",
            Name = "Test",
            Provider = provider,
            Cost = new ModelCost { Prompt = 0, Completion = 0, InputCacheRead = 0, InputCacheWrite = 0 },
            ContextWindow = 8000,
            Input = ModelModalities.Text,
            Output = ModelModalities.Text,
            Parameters = ModelParameters.Temperature,
        };
        var ctx = new CompletionContext { Messages = [new CompletionUserTextMessage { Text = "hi" }] };

        var stream = provider.GetCompletions(model, ctx);
        await foreach (var _ in stream) { /* drain */ }
        return await stream.ResultAsync;
    }

    [Fact]
    public async Task CacheWriteTokens_are_mapped_and_excluded_from_input()
    {
        var result = await RunWithUsageAsync(
            "{\"prompt_tokens\":1000,\"completion_tokens\":50,\"total_tokens\":1050,\"prompt_tokens_details\":{\"cached_tokens\":200,\"cache_write_tokens\":100}}");

        var usage = result.CompletionUsage;
        Assert.Equal(100, usage.CacheWrite);
        Assert.Equal(200, usage.CacheRead);
        Assert.Equal(700, usage.Input); // 1000 - 200 read - 100 write
        Assert.Equal(50, usage.Output);
    }

    [Fact]
    public async Task Usage_without_cache_write_keeps_existing_read_behavior()
    {
        var result = await RunWithUsageAsync(
            "{\"prompt_tokens\":1000,\"completion_tokens\":50,\"total_tokens\":1050,\"prompt_tokens_details\":{\"cached_tokens\":200}}");

        var usage = result.CompletionUsage;
        Assert.Equal(0, usage.CacheWrite);
        Assert.Equal(200, usage.CacheRead);
        Assert.Equal(800, usage.Input); // 1000 - 200 read
    }
}
