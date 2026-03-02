using System.Net.Http.Json;
using System.Text.Json;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

public sealed class OpenRouterClient(HttpClient httpClient)
{
    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    private readonly HttpClient _httpClient = httpClient;

    public async Task<OpenRouterModelsResponse> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models";

        var response = await _httpClient.GetFromJsonAsync(
            requestUri,
            OpenRouterJsonContext.Default.OpenRouterModelsResponse,
            cancellationToken).ConfigureAwait(false);

        return response ?? throw new JsonException(
            "Deserialized OpenRouterModelsResponse was null.");
    }

    public async Task<int> GetModelsCountAsync(
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models/count";

        var response = await _httpClient.GetFromJsonAsync(
            requestUri,
            OpenRouterJsonContext.Default.OpenRouterModelsCountResponse,
            cancellationToken).ConfigureAwait(false);

        return response?.Data.Count ?? throw new JsonException(
            "Deserialized OpenRouterModelsCountResponse was null.");
    }

    private string GetBaseUrl()
    {
        return _httpClient.BaseAddress is not null
            ? _httpClient.BaseAddress.ToString().TrimEnd('/')
            : DefaultBaseUrl;
    }
}
