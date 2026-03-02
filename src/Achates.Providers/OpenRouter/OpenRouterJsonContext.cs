using System.Text.Json.Serialization;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

[JsonSerializable(typeof(OpenRouterModelsResponse))]
[JsonSerializable(typeof(OpenRouterModelsCountResponse))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class OpenRouterJsonContext : JsonSerializerContext;
