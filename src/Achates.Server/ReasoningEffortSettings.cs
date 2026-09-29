using Achates.Providers.Models;

namespace Achates.Server;

internal static class ReasoningEffortSettings
{
    // Preserve the old regular-model fallback without confusing it with an explicit
    // request to use the provider's default. Thinking calls have no implicit effort.
    internal static string Regular(CompletionConfig? completion) =>
        completion is null ? "default" : completion.ReasoningEffort ?? "medium";

    internal static string? Resolve(string? effort, Model model) =>
        !model.Parameters.HasFlag(ModelParameters.ReasoningEffort)
        || string.IsNullOrWhiteSpace(effort)
        || effort.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : effort;
}
