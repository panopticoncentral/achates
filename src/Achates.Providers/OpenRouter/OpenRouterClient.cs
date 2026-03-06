using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

public sealed class OpenRouterClient(HttpClient httpClient, string apiKey)
{
    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    private void SetAuth(HttpRequestMessage request) =>
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

    public async Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        SetAuth(request);

        using var httpResponse = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();

        var response = await httpResponse.Content.ReadFromJsonAsync(
            OpenRouterJsonContext.Default.OpenRouterModelsResponse,
            cancellationToken).ConfigureAwait(false);

        return response?.Data ?? [];
    }

    public async Task<int> GetModelsCountAsync(CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models/count";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        SetAuth(request);

        using var httpResponse = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();

        var response = await httpResponse.Content.ReadFromJsonAsync(
            OpenRouterJsonContext.Default.OpenRouterModelsCountResponse,
            cancellationToken).ConfigureAwait(false);

        return response?.Data.Count ?? -1;
    }

    public async Task<ChatCompletionResponse?> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        SetAuth(httpRequest);
        httpRequest.Content = JsonContent.Create(
            request,
            OpenRouterJsonContext.Default.ChatCompletionRequest);

        using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

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
        SetAuth(httpRequest);
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

            // Check for inline error events before parsing as a chunk.
            // OpenRouter can send errors mid-stream as: data: {"error":{...}}
            using var doc = JsonDocument.Parse(data.ToString());
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                var error = errorElement.Deserialize(
                    OpenRouterJsonContext.Default.ChatErrorDetail);
                throw new OpenRouterException(
                    FormatErrorMessage(
                        error?.Message ?? "Unknown streaming error",
                        error?.Metadata),
                    error?.Code ?? 0,
                    error?.Metadata);
            }

            var chunk = doc.RootElement.Deserialize(
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
            FormatErrorMessage(
                error?.Error.Message ?? $"HTTP {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}",
                error?.Error.Metadata),
            error?.Error.Code ?? (int)httpResponse.StatusCode,
            error?.Error.Metadata);
    }

    internal static string FormatErrorMessage(string message, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } meta)
            return message;

        // OpenRouter often puts the upstream provider's error in metadata.raw
        if (meta.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
        {
            var rawText = raw.GetString();
            if (!string.IsNullOrWhiteSpace(rawText) && rawText != message)
                return $"{message} — {rawText}";
        }

        // Some errors include a provider_name
        if (meta.TryGetProperty("provider_name", out var provider) && provider.ValueKind == JsonValueKind.String)
        {
            var providerName = provider.GetString();
            if (!string.IsNullOrWhiteSpace(providerName))
                return $"{message} (via {providerName})";
        }

        return message;
    }

    private string GetBaseUrl()
    {
        return httpClient.BaseAddress is not null
            ? httpClient.BaseAddress.ToString().TrimEnd('/')
            : DefaultBaseUrl;
    }
}
