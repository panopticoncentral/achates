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
}
