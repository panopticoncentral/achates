using Achates.Server;

namespace Achates.Tests;

public class ConfigLoaderTests
{
    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"achates-cfg-{Guid.NewGuid():N}.yaml");

    [Fact]
    public void Models_RoundTrip_PreservesBaseAndThinking()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Models = new ModelsConfig { Base = "anthropic/claude-sonnet-4.6", Thinking = "anthropic/claude-opus-4.7" },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var loaded = ConfigLoader.Load();

            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models?.Base);
            Assert.Equal("anthropic/claude-opus-4.7", loaded.Models?.Thinking);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Models_NullThinking_OmittedAndLoadsAsNull()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Models = new ModelsConfig { Base = "anthropic/claude-sonnet-4.6", Thinking = null },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var text = File.ReadAllText(path);
            var loaded = ConfigLoader.Load();

            Assert.DoesNotContain("thinking", text);
            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models?.Base);
            Assert.Null(loaded.Models?.Thinking);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }
}
