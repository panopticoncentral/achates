using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatCompletionError
{
    [JsonPropertyName("error")]
    public required OpenRouterChatErrorDetail Error { get; init; }
}

internal sealed record OpenRouterChatErrorDetail
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}
