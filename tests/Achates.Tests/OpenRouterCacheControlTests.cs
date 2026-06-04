using System.Net;
using System.Text;
using System.Text.Json;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Providers.OpenRouter;

namespace Achates.Tests;

/// <summary>
/// Anthropic models on OpenRouter only cache a prompt prefix when the request marks
/// it with an explicit <c>cache_control</c> breakpoint (unlike OpenAI/DeepSeek, which
/// cache automatically). These tests pin that the provider emits ephemeral breakpoints
/// on the system prompt and the final message for <c>anthropic/*</c> models, and leaves
/// other providers' requests untouched.
/// </summary>
public class OpenRouterCacheControlTests
{
    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidStreamBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private const string ValidStreamBody =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n"
        + "data: [DONE]\n\n";

    private static (OpenRouterProvider Provider, Model Model) MakeProvider(
        CapturingHttpHandler handler, string modelId)
    {
        var provider = new OpenRouterProvider
        {
            HttpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example/api/v1/") },
            Key = "test-key",
        };
        var model = new Model
        {
            Id = modelId,
            Name = "Test",
            Provider = provider,
            Cost = new ModelCost { Prompt = 0, Completion = 0 },
            ContextWindow = 8000,
            Input = ModelModalities.Text,
            Output = ModelModalities.Text,
            Parameters = ModelParameters.Temperature,
        };
        return (provider, model);
    }

    private static CompletionContext Ctx() => new()
    {
        SystemPrompt = "You are Claire.",
        Messages = [new CompletionUserTextMessage { Text = "hi" }],
    };

    private static async Task DrainAsync(CompletionEventStream stream)
    {
        await foreach (var _ in stream) { /* drain */ }
        await stream.ResultAsync;
    }

    private static async Task<JsonDocument> CaptureRequestAsync(string modelId)
    {
        var handler = new CapturingHttpHandler();
        var (provider, model) = MakeProvider(handler, modelId);
        await DrainAsync(provider.GetCompletions(model, Ctx()));
        Assert.NotNull(handler.LastBody);
        return JsonDocument.Parse(handler.LastBody!);
    }

    [Fact]
    public async Task AnthropicModel_SystemPrompt_carries_ephemeral_cache_breakpoint()
    {
        using var doc = await CaptureRequestAsync("anthropic/claude-sonnet-4.6");

        var system = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("system", system.GetProperty("role").GetString());

        // Content must be an array of parts (not a bare string) so the breakpoint can attach.
        var content = system.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var lastPart = content[content.GetArrayLength() - 1];
        Assert.Equal("ephemeral", lastPart.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnthropicModel_LastMessage_carries_ephemeral_cache_breakpoint()
    {
        using var doc = await CaptureRequestAsync("anthropic/claude-sonnet-4.6");

        var messages = doc.RootElement.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];

        var content = last.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var lastPart = content[content.GetArrayLength() - 1];
        Assert.Equal("ephemeral", lastPart.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnthropicModel_multipart_last_message_keeps_parts_and_marks_only_the_last()
    {
        // A multimodal final message already has array content; the breakpoint must append
        // to the last part without dropping the earlier parts or marking them.
        var handler = new CapturingHttpHandler();
        var provider = new OpenRouterProvider
        {
            HttpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example/api/v1/") },
            Key = "test-key",
        };
        var model = new Model
        {
            Id = "anthropic/claude-sonnet-4.6",
            Name = "Test",
            Provider = provider,
            Cost = new ModelCost { Prompt = 0, Completion = 0 },
            ContextWindow = 8000,
            Input = ModelModalities.Text | ModelModalities.Image,
            Output = ModelModalities.Text,
            Parameters = ModelParameters.Temperature,
        };
        var ctx = new CompletionContext
        {
            SystemPrompt = "You are Claire.",
            Messages =
            [
                new CompletionUserContentMessage
                {
                    Content =
                    [
                        new CompletionTextContent { Text = "look at this" },
                        new CompletionImageContent { Data = "base64data", MimeType = "image/png" },
                    ],
                },
            ],
        };

        await DrainAsync(provider.GetCompletions(model, ctx));
        using var doc = JsonDocument.Parse(handler.LastBody!);

        var messages = doc.RootElement.GetProperty("messages");
        var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.False(content[0].TryGetProperty("cache_control", out _)); // text part untouched
        Assert.Equal("ephemeral", content[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NonAnthropicModel_has_no_cache_control()
    {
        using var doc = await CaptureRequestAsync("openai/gpt-5");

        // OpenAI/DeepSeek auto-cache; we must not inject cache_control for them.
        Assert.DoesNotContain("cache_control", doc.RootElement.GetRawText());
    }
}
