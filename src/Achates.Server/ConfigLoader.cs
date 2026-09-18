using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Achates.Server;

public static class ConfigLoader
{
    public static readonly string DefaultConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates");

    /// <summary>Root for configuration and persistent data; set before starting the server.</summary>
    public static string DataDir => ResolveDataDir(Environment.GetEnvironmentVariable("ACHATES_HOME"));

    public static string DefaultConfigPath => Path.Combine(DataDir, "config.yaml");

    public static string ResolveDataDir(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return DefaultConfigDir;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home == "~")
            home = userHome;
        else if (home.StartsWith("~/", StringComparison.Ordinal))
            home = Path.Combine(userHome, home[2..]);

        return Path.GetFullPath(home);
    }

    /// <summary>
    /// Load config from ACHATES_CONFIG_PATH, or config.yaml under ACHATES_HOME (default ~/.achates).
    /// Creates a default config file if one does not exist.
    /// </summary>
    public static AchatesConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH")
                   ?? DefaultConfigPath;

        if (!File.Exists(path))
        {
            var config = CreateDefault();
            Save(config, path);
            return config;
        }

        var yaml = File.ReadAllText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<AchatesConfig>(yaml) ?? new AchatesConfig();
    }

    public static void Save(AchatesConfig config, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH")
                 ?? DefaultConfigPath;

        var dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
            Directory.CreateDirectory(dir);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        File.WriteAllText(path, serializer.Serialize(config));
    }

    private static AchatesConfig CreateDefault() => new()
    {
        Provider = new ProviderConfig { Name = "openrouter" },
    };
}
