using System.Security.Cryptography;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Achates.Server.Tools;

internal static class MonetaAccess
{
    internal static bool CanChat(string initiator, string target,
        IReadOnlyDictionary<string, AgentDefinition> agents, Func<MonetaConfig?>? readConfig = null)
    {
        try
        {
            var config = (readConfig ?? LoadConfig)();
            bool Protected(string id) => config?.ApprovedAgents?.ContainsKey(id) == true ||
                (agents.TryGetValue(id, out var def) && def.ToolNames.Contains("moneta"));
            if (!Protected(initiator) && !Protected(target)) return true;
            bool Approved(string id) => agents.TryGetValue(id, out var def) &&
                def.Tools.OfType<MonetaTool>().Any(tool => tool.IsApproved(config));
            return Approved(initiator) && Approved(target);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    internal static string DefinitionHash(string agentFile) =>
        ContentHash(File.ReadAllText(agentFile));

    internal static string ContentHash(string definition) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(definition)));

    // Unlike ConfigLoader.Load, a missing file must fail closed without creating defaults.
    internal static MonetaConfig? LoadConfig()
    {
        var path = Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH") ?? ConfigLoader.DefaultConfigPath;
        return ReadConfig(path);
    }

    internal static MonetaConfig? ReadConfig(string path) => new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties().Build()
        .Deserialize<AchatesConfig>(File.ReadAllText(path))?.Tools?.Moneta;

    internal static bool IsApproved(MonetaConfig? config, string agentId, string agentFile, string loadedHash)
    {
        if (config?.ApprovedAgents is null ||
            !config.ApprovedAgents.TryGetValue(agentId, out var approvedHash) ||
            string.IsNullOrWhiteSpace(approvedHash) || approvedHash.Length != 64 ||
            !string.Equals(approvedHash, loadedHash, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(approvedHash, DefinitionHash(agentFile), StringComparison.OrdinalIgnoreCase);
    }
}
