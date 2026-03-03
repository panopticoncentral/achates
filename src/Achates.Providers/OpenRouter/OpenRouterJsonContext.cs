using System.Text.Json.Serialization;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

[JsonSerializable(typeof(OpenRouterModelsResponse))]
[JsonSerializable(typeof(OpenRouterModelsCountResponse))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ChatCompletionError))]
[JsonSerializable(typeof(IReadOnlyList<ChatContentPart>))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class OpenRouterJsonContext : JsonSerializerContext;
