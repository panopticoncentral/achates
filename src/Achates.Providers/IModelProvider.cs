using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Providers;

/// <summary>
/// A provider of models and completions.
/// </summary>
public interface IModelProvider
{
    /// <summary>
    /// The provider's ID.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The provider's environment variable for its API key.
    /// </summary>
    string EnvironmentKey { get; }

    /// <summary>
    /// The HTTP client used for this provider's API calls.
    /// </summary>
    HttpClient HttpClient { set; }

    /// <summary>
    /// The API key for this provider.
    /// </summary>
    string Key { set; }

    /// <summary>
    /// Gets all models available from this provider.
    /// </summary>
    Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream a completion, returning an event stream that emits events as content is generated.
    /// </summary>
    CompletionEventStream GetCompletions(Model model, CompletionContext completionContext, CompletionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an image from a text prompt, optionally using a reference image for refinement.
    /// Returns raw image bytes, or null if unsupported.
    /// </summary>
    Task<byte[]?> GenerateImageAsync(string modelId, string prompt, byte[]? referenceImage = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);
}
