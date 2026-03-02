using Achates.Providers.Browser.Formatting;
using Achates.Providers.Browser.ViewModels;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Achates.Providers.Browser.Views;

public sealed class ModelDetailView : FrameView
{
    private readonly TextView _content;

    public ModelDetailView()
    {
        Title = "Model Details";

        _content = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };

        Add(_content);
    }

    public void ShowModel(ModelRowData? row)
    {
        if (row is null)
        {
            _content.Text = "Select a model to view details.";
            return;
        }

        var model = row.Source;
        var lines = new List<string>
        {
            $"Name: {model.Name}",
            $"ID: {model.Id}",
            $"Slug: {model.CanonicalSlug}",
            $"Hugging Face: {model.HuggingFaceId ?? "N/A"}",
            $"Created: {DateTimeFormatter.FromUnixTimestamp(model.Created)}",
            $"Context Length: {ContextLengthFormatter.Format(model.ContextLength)}",
            "",
            "--- Description ---",
            model.Description ?? "No description available.",
            "",
            "--- Pricing (per million tokens) ---",
            $"  Prompt:             {PricingFormatter.FormatPerToken(model.Pricing?.Prompt)}",
            $"  Completion:         {PricingFormatter.FormatPerToken(model.Pricing?.Completion)}",
            $"  Image:              {PricingFormatter.FormatPerToken(model.Pricing?.Image)}",
            $"  Audio:              {PricingFormatter.FormatPerToken(model.Pricing?.Audio)}",
            $"  Request:            {PricingFormatter.FormatPerToken(model.Pricing?.Request)}",
            $"  Web Search:         {PricingFormatter.FormatPerToken(model.Pricing?.WebSearch)}",
            $"  Internal Reasoning: {PricingFormatter.FormatPerToken(model.Pricing?.InternalReasoning)}",
            $"  Cache Read:         {PricingFormatter.FormatPerToken(model.Pricing?.InputCacheRead)}",
            $"  Cache Write:        {PricingFormatter.FormatPerToken(model.Pricing?.InputCacheWrite)}",
            "",
            "--- Architecture ---",
            $"  Modality: {model.Architecture?.Modality ?? "N/A"}",
            $"  Input:    {FormatList(model.Architecture?.InputModalities)}",
            $"  Output:   {FormatList(model.Architecture?.OutputModalities)}",
            $"  Tokenizer: {model.Architecture?.Tokenizer ?? "N/A"}",
            $"  Instruct Type: {model.Architecture?.InstructType ?? "N/A"}"
        };

        if (model.TopProvider is not null)
        {
            lines.AddRange([
                "",
                "--- Top Provider ---",
                $"  Context Length: {model.TopProvider.ContextLength?.ToString() ?? "N/A"}",
                $"  Max Completion: {model.TopProvider.MaxCompletionTokens?.ToString() ?? "N/A"}",
                $"  Moderated: {model.TopProvider.IsModerated}"
            ]);
        }

        if (model.SupportedParameters is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("--- Supported Parameters ---");
            lines.Add($"  {string.Join(", ", model.SupportedParameters)}");
        }

        if (model.ExpirationDate is not null)
        {
            lines.Add("");
            lines.Add($"Expiration: {model.ExpirationDate}");
        }

        _content.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatList(IReadOnlyList<string>? items) =>
        items is { Count: > 0 } ? string.Join(", ", items) : "N/A";
}
