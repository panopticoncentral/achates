using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

public sealed class OpenRouterClient(HttpClient httpClient)
{
    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    public async Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models";

        var response = await httpClient.GetFromJsonAsync(
            requestUri,
            OpenRouterJsonContext.Default.OpenRouterModelsResponse,
            cancellationToken).ConfigureAwait(false);

        return response?.Data ?? [];
    }

    public async Task<int> GetModelsCountAsync(CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models/count";

        var response = await httpClient.GetFromJsonAsync(
            requestUri,
            OpenRouterJsonContext.Default.OpenRouterModelsCountResponse,
            cancellationToken).ConfigureAwait(false);

        return response?.Data.Count ?? -1;
    }

    public async Task<ChatCompletionResponse?> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/chat/completions";

        using var httpResponse = await httpClient.PostAsJsonAsync(
            requestUri,
            request,
            OpenRouterJsonContext.Default.ChatCompletionRequest,
            cancellationToken).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            await ThrowForErrorResponseAsync(httpResponse, cancellationToken)
                .ConfigureAwait(false);
        }

        return await httpResponse.Content.ReadFromJsonAsync(
            OpenRouterJsonContext.Default.ChatCompletionResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        httpRequest.Content = JsonContent.Create(
            request,
            OpenRouterJsonContext.Default.ChatCompletionRequest);

        using var httpResponse = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            await ThrowForErrorResponseAsync(httpResponse, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var stream = await httpResponse.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken)
                   .ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line.AsSpan(6);

            if (data is "[DONE]")
            {
                yield break;
            }

            var chunk = JsonSerializer.Deserialize(
                data,
                OpenRouterJsonContext.Default.ChatCompletionChunk);

            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    private static async Task ThrowForErrorResponseAsync(
        HttpResponseMessage httpResponse,
        CancellationToken cancellationToken)
    {
        ChatCompletionError? error = null;

        try
        {
            error = await httpResponse.Content.ReadFromJsonAsync(
                OpenRouterJsonContext.Default.ChatCompletionError,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Response body was not a valid error object; fall through.
        }

        throw new OpenRouterException(
            error?.Error.Message ?? $"HTTP {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}",
            error?.Error.Code ?? (int)httpResponse.StatusCode,
            error?.Error.Metadata);
    }

    private string GetBaseUrl()
    {
        return httpClient.BaseAddress is not null
            ? httpClient.BaseAddress.ToString().TrimEnd('/')
            : DefaultBaseUrl;
    }
}
