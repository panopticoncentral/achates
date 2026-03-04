namespace Achates.Providers.Models;

public sealed record Model
{
    /// <summary>
    /// The ID of the model, e.g. "google/gemini-3.1-flash-lit-preview"
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The human name of the model, e.g. "Google: Gemini 3.1 Flash Lite Preview"
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// A description of the model.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The model's provider.
    /// </summary>
    public required IModelProvider Provider { get; init; }

    /// <summary>
    /// Model cost information.
    /// </summary>
    public required ModelCost Cost { get; init; }

    /// <summary>
    /// Maximum context length in tokens.
    /// </summary>
    public required int ContextWindow { get; init; }

    /// <summary>
    /// The input modalities this model accepts.
    /// </summary>
    public required ModelModalities Input { get; init; }

    /// <summary>
    /// The modalities this model can output.
    /// </summary>
    public required ModelModalities Output { get; init; }

    /// <summary>
    /// The parameters this model supports.
    /// </summary>
    public required ModelParameters Parameters { get; init; }

    public bool Equals(Model? other) => other is not null && Id == other.Id && Provider.Id == other.Provider.Id;

    public override int GetHashCode() => HashCode.Combine(Id, Provider.Id);
}
