using Achates.Providers.OpenRouter;

namespace Achates.Providers;

public static class ModelProviders
{
    private static readonly Dictionary<string, Func<IModelProvider>> _factories = new()
    {
        ["openrouter"] = () => new OpenRouterProvider(),
    };

    public static IModelProvider? Create(string id)
    {
        return _factories.TryGetValue(id, out var factory) ? factory() : null;
    }
}
