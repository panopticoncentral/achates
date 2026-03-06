using System.Text.Json.Serialization;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

[JsonSerializable(typeof(OpenRouterModelsResponse))]
[JsonSerializable(typeof(OpenRouterModelsCountResponse))]
[JsonSerializable(typeof(OpenRouterChatCompletionRequest))]
[JsonSerializable(typeof(OpenRouterChatCompletionResponse))]
[JsonSerializable(typeof(OpenRouterChatCompletionChunk))]
[JsonSerializable(typeof(OpenRouterChatCompletionError))]
[JsonSerializable(typeof(OpenRouterChatErrorDetail))]
[JsonSerializable(typeof(IReadOnlyList<OpenRouterChatContentPart>))]
[JsonSerializable(typeof(OpenRouterChatInputAudio))]
[JsonSerializable(typeof(OpenRouterChatAudioConfig))]
[JsonSerializable(typeof(OpenRouterChatAudioDelta))]
[JsonSerializable(typeof(OpenRouterChatAudioResponse))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class OpenRouterJsonContext : JsonSerializerContext;
