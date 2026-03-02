using Achates.Providers.Browser.Formatting;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.Browser.ViewModels;

public sealed record ModelRowData
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string ContextLength { get; init; }
    public required string InputPrice { get; init; }
    public required string OutputPrice { get; init; }
    public required string Modality { get; init; }
    public required string Created { get; init; }
    public required OpenRouterModel Source { get; init; }

    public static ModelRowData FromModel(OpenRouterModel model) => new()
    {
        Name = model.Name,
        Id = model.Id,
        ContextLength = ContextLengthFormatter.Format(model.ContextLength),
        InputPrice = PricingFormatter.FormatPerToken(model.Pricing?.Prompt),
        OutputPrice = PricingFormatter.FormatPerToken(model.Pricing?.Completion),
        Modality = model.Architecture?.Modality ?? "unknown",
        Created = DateTimeFormatter.FromUnixTimestamp(model.Created),
        Source = model
    };
}
