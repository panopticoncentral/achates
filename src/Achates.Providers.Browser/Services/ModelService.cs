using Achates.Providers.OpenRouter;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.Browser.Services;

public sealed class ModelService(HttpClient httpClient)
{
    private readonly OpenRouterClient _client = new(httpClient);

    public async Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return response.Data;
    }
}
