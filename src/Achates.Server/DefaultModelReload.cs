namespace Achates.Server;

/// <summary>
/// Computes which agents must be reloaded when a global default model changes.
/// An agent relies on the global base when it has no per-agent <see cref="AgentConfig.Model"/>;
/// it relies on the global thinking model when it has no <see cref="AgentConfig.ThinkingModel"/>
/// AND has the <c>think</c> tool (only those resolve a thinking model).
/// </summary>
public static class DefaultModelReload
{
    public static HashSet<string> AgentsToReload(
        IEnumerable<(string Name, AgentConfig Config)> agents,
        bool baseChanged,
        bool thinkingChanged)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!baseChanged && !thinkingChanged)
            return result;

        foreach (var (name, cfg) in agents)
        {
            if (baseChanged && string.IsNullOrWhiteSpace(cfg.Model))
                result.Add(name);

            if (thinkingChanged
                && string.IsNullOrWhiteSpace(cfg.ThinkingModel)
                && cfg.Tools?.Contains("think") == true)
                result.Add(name);
        }

        return result;
    }
}
