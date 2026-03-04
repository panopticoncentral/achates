using System.Globalization;
using Achates.Providers.Models;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

internal sealed class OpenRouterProvider : IModelProvider
{
    public string Id => "openrouter";

    public string EnvironmentKey => "OPENROUTER_API_KEY";

    public HttpClient HttpClient { private get; set; } = null!;

    public string Key { private get; set; } = string.Empty;

    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var client = new OpenRouterClient(HttpClient, Key);
        var orModels = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);

        var models = new List<Model>(orModels.Count);
        models.AddRange(orModels.Select(or => new Model
        {
            Id = or.Id,
            Name = or.Name,
            Description = or.Description,
            Provider = this,
            Cost = MapCost(or.Pricing),
            ContextWindow = or.ContextLength,
            Input = MapModalities(or.Architecture?.InputModalities),
            Output = MapModalities(or.Architecture?.OutputModalities),
            Parameters = MapParameters(or.SupportedParameters),
        }));

        return models;
    }

    private static ModelCost MapCost(OpenRouterPricing? pricing)
    {
        if (pricing is null)
        {
            return new ModelCost { Prompt = 0, Completion = 0 };
        }

        return new ModelCost
        {
            Prompt = ParseDecimal(pricing.Prompt),
            Completion = ParseDecimal(pricing.Completion),
            Request = ParseNullableDecimal(pricing.Request),
            Image = ParseNullableDecimal(pricing.Image),
            ImageToken = ParseNullableDecimal(pricing.ImageToken),
            ImageOutput = ParseNullableDecimal(pricing.ImageOutput),
            Audio = ParseNullableDecimal(pricing.Audio),
            AudioOutput = ParseNullableDecimal(pricing.AudioOutput),
            InputAudioCache = ParseNullableDecimal(pricing.InputAudioCache),
            WebSearch = ParseNullableDecimal(pricing.WebSearch),
            InternalReasoning = ParseNullableDecimal(pricing.InternalReasoning),
            InputCacheRead = ParseNullableDecimal(pricing.InputCacheRead),
            InputCacheWrite = ParseNullableDecimal(pricing.InputCacheWrite),
            Discount = pricing.Discount,
        };
    }

    private static ModelModalities MapModalities(IReadOnlyList<string>? modalities)
    {
        if (modalities is null or { Count: 0 })
        {
            return ModelModalities.Text;
        }

        var result = ModelModalities.Text;
        foreach (var modality in modalities)
        {
            result |= modality switch
            {
                "image" => ModelModalities.Image,
                "file" => ModelModalities.File,
                "audio" => ModelModalities.Audio,
                "video" => ModelModalities.Video,
                "embeddings" => ModelModalities.Embeddings,
                _ => 0,
            };
        }

        return result;
    }

    private static ModelParameters MapParameters(IReadOnlyList<string>? parameters)
    {
        if (parameters is null or { Count: 0 })
        {
            return ModelParameters.Temperature;
        }

        var result = ModelParameters.Temperature;
        foreach (var param in parameters)
        {
            result |= param switch
            {
                "temperature" => ModelParameters.Temperature,
                "top_p" => ModelParameters.TopP,
                "top_k" => ModelParameters.TokK,
                "min_p" => ModelParameters.MinP,
                "top_a" => ModelParameters.TopA,
                "frequency_penalty" => ModelParameters.FrequencyPenality,
                "presence_penalty" => ModelParameters.PresencePenality,
                "repetition_penalty" => ModelParameters.RepetitionPenality,
                "max_tokens" => ModelParameters.MaxTokens,
                "logit_bias" => ModelParameters.LogitBias,
                "logprobs" => ModelParameters.LogProbs,
                "top_logprobs" => ModelParameters.TopLogProbs,
                "seed" => ModelParameters.Seed,
                "response_format" => ModelParameters.ResponseFormat,
                "structured_outputs" => ModelParameters.StructuredOutputs,
                "stop" => ModelParameters.Stop,
                "tools" => ModelParameters.Tools,
                "tool_choice" => ModelParameters.ToolChoice,
                "parallel_tool_calls" => ModelParameters.ParallelToolCalls,
                "include_reasoning" => ModelParameters.IncludeReasoning,
                "reasoning" => ModelParameters.Reasoning,
                "reasoning_effort" => ModelParameters.ReasoningEffort,
                "web_search_options" => ModelParameters.WebSearchOptions,
                "verbosity" => ModelParameters.Verbosity,
                _ => 0,
            };
        }

        return result;
    }

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static decimal? ParseNullableDecimal(string? value) =>
        decimal.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : null;
}
