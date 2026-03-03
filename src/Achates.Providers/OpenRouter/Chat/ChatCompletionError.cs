using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatCompletionError
{
    [JsonPropertyName("error")]
    public required ChatErrorDetail Error { get; init; }
}

public sealed record ChatErrorDetail
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}
