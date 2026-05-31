using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

internal sealed class OpenRouterClient(HttpClient httpClient, string apiKey)
{
    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    private const int MaxAttempts = 3;

    internal static Func<int, TimeSpan> RetryDelay { get; set; } = DefaultRetryDelay;

    /// <summary>
    /// Maximum time an in-progress SSE stream may go FULLY silent — no bytes at all,
    /// not even an OpenRouter ": PROCESSING" keepalive — before it is aborted as a dead
    /// connection (see <see cref="StreamIdleTimeoutException"/>). Keepalives reset it,
    /// so a keepalived-but-slow upstream is deliberately NOT aborted here (live data
    /// showed a recovering turn can pause 6+ min between chunks and still finish). Genuine
    /// upstream deaths are reported by OpenRouter as an error and recovered via the
    /// provider's turn-replay retry; this is only a black-hole backstop.
    /// </summary>
    internal static TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private static TimeSpan DefaultRetryDelay(int attempt)
    {
        // 500ms, 2s, 8s base, multiplied by a jitter factor uniform on [0.75, 1.25].
        var baseMs = 500 * Math.Pow(4, attempt);
        var jitter = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(baseMs * jitter);
    }

    private static bool IsTransient502(OpenRouterException ex)
    {
        if (ex.Code == 502) return true;
        if (ex.Metadata is not { ValueKind: JsonValueKind.Object } meta) return false;
        if (!meta.TryGetProperty("error_type", out var t)) return false;
        return t.ValueKind == JsonValueKind.String
            && t.GetString() == "provider_unavailable";
    }

    private void SetAuth(HttpRequestMessage request) =>
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

    public async Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(
        IReadOnlyDictionary<string, string>? queryParams = null,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{GetBaseUrl()}/models";
        if (queryParams is { Count: > 0 })
        {
            var qs = string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            requestUri = $"{requestUri}?{qs}";
        }

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

    public async Task<OpenRouterChatCompletionResponse?> CreateOpenRouterChatCompletionAsync(
        OpenRouterChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendCompletionOnceAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OpenRouterException ex) when (
                IsTransient502(ex) && attempt < MaxAttempts - 1)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<OpenRouterChatCompletionResponse?> SendCompletionOnceAsync(
        OpenRouterChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var requestUri = $"{GetBaseUrl()}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        SetAuth(httpRequest);
        httpRequest.Content = JsonContent.Create(
            request,
            OpenRouterJsonContext.Default.OpenRouterChatCompletionRequest);

        using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            await ThrowForErrorResponseAsync(httpResponse, cancellationToken)
                .ConfigureAwait(false);
        }

        return await httpResponse.Content.ReadFromJsonAsync(
            OpenRouterJsonContext.Default.OpenRouterChatCompletionResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<OpenRouterChatCompletionChunk> StreamOpenRouterChatCompletionAsync(
        OpenRouterChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var yieldedAny = false;
        for (var attempt = 0; ; attempt++)
        {
            // Token already bound to the iterator via [EnumeratorCancellation];
            // GetAsyncEnumerator gets default to avoid threading the same token twice.
            await using var inner = StreamOneAttemptAsync(request, cancellationToken)
                .GetAsyncEnumerator();
            var shouldRetry = false;

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await inner.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OpenRouterException ex) when (
                    IsTransient502(ex)
                    && !yieldedAny
                    && attempt < MaxAttempts - 1)
                {
                    shouldRetry = true;
                    break;
                }

                if (!hasNext) break;
                yieldedAny = true;
                yield return inner.Current;
            }

            if (!shouldRetry) yield break;
            await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<OpenRouterChatCompletionChunk> StreamOneAttemptAsync(
        OpenRouterChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestUri = $"{GetBaseUrl()}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        SetAuth(httpRequest);
        httpRequest.Content = JsonContent.Create(
            request,
            OpenRouterJsonContext.Default.OpenRouterChatCompletionRequest);

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

        while (true)
        {
            string? line;

            // Per-read idle timeout — a TRUE-silence backstop only. It aborts the
            // stream when *no bytes at all* arrive for StreamIdleTimeout. Crucially,
            // OpenRouter's ": OPENROUTER PROCESSING" keepalive comments DO reset it
            // (every line resets it), so a keepalived-but-slow upstream is NOT
            // aborted here — live data showed a recovering turn can go silent for
            // 6+ minutes between chunks and still complete. Genuinely dead upstreams
            // are reported by OpenRouter as an error and recovered by the provider's
            // turn-replay retry; this timer only catches a black hole that sends
            // nothing whatsoever (mainly relevant to interactive turns with no
            // cron wall-clock cap).
            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idleCts.CancelAfter(StreamIdleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new StreamIdleTimeoutException(StreamIdleTimeout);
                }
            }

            if (line is null)
            {
                break;
            }

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
                    OpenRouterJsonContext.Default.OpenRouterChatErrorDetail);
                throw new OpenRouterException(
                    FormatErrorMessage(
                        error?.Message ?? "Unknown streaming error",
                        error?.Metadata),
                    error?.Code ?? 0,
                    error?.Metadata);
            }

            var chunk = doc.RootElement.Deserialize(
                OpenRouterJsonContext.Default.OpenRouterChatCompletionChunk);

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
        OpenRouterChatCompletionError? error = null;

        try
        {
            error = await httpResponse.Content.ReadFromJsonAsync(
                OpenRouterJsonContext.Default.OpenRouterChatCompletionError,
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

    private static string FormatErrorMessage(string message, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } meta)
        {
            return message;
        }

        // OpenRouter often puts the upstream provider's error in metadata.raw
        if (meta.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
        {
            var rawText = raw.GetString();
            if (!string.IsNullOrWhiteSpace(rawText) && rawText != message)
            {
                return $"{message} — {rawText}";
            }
        }

        // Some errors include a provider_name
        if (!meta.TryGetProperty("provider_name", out var provider) || provider.ValueKind != JsonValueKind.String)
        {
            return message;
        }

        var providerName = provider.GetString();
        return !string.IsNullOrWhiteSpace(providerName) ? $"{message} (via {providerName})" : message;
    }

    private string GetBaseUrl()
    {
        return httpClient.BaseAddress is not null
            ? httpClient.BaseAddress.ToString().TrimEnd('/')
            : DefaultBaseUrl;
    }
}
