using Achates.Server;

namespace Achates.Tests;

public sealed class AgentLoaderTests
{
    [Theory]
    [InlineData("Paul", "paul")]
    [InlineData("Paul's Assistant", "pauls-assistant")]
    [InlineData("My Agent 2", "my-agent-2")]
    [InlineData("Valerie", "valerie")]
    [InlineData("  Spaces  ", "spaces")]
    [InlineData("--dashes--", "dashes")]
    [InlineData("a--b", "a-b")]
    [InlineData("UPPER CASE", "upper-case")]
    [InlineData("hello world!", "hello-world")]
    [InlineData("René", "ren")]
    [InlineData("", null)]
    [InlineData("!!!", null)]
    public void NormalizeId_ProducesExpectedResult(string input, string? expected)
    {
        var result = AgentLoader.NormalizeId(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeId_TruncatesAt64Characters()
    {
        var longName = new string('a', 100);
        var result = AgentLoader.NormalizeId(longName);
        Assert.NotNull(result);
        Assert.Equal(64, result!.Length);
    }

    [Fact]
    public void RenameAgent_OnDisk_MovesDirectoryAndUpdatesCrossReferences()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"achates-rename-test-{Guid.NewGuid():N}");
        try
        {
            // Setup: create two agent directories
            var paulDir = Path.Combine(basePath, "agents", "paul");
            var valDir = Path.Combine(basePath, "agents", "val");
            Directory.CreateDirectory(paulDir);
            Directory.CreateDirectory(valDir);

            File.WriteAllText(Path.Combine(paulDir, "AGENT.md"), """
                # Paul

                Personal assistant.

                ## Capabilities

                **Model:** anthropic/claude-sonnet-4
                """.Replace("                ", ""));

            File.WriteAllText(Path.Combine(valDir, "AGENT.md"), """
                # Val

                Another assistant.

                ## Capabilities

                **Model:** anthropic/claude-sonnet-4

                **Allowed Chats:**
                  - paul
                """.Replace("                ", ""));

            // Act: rename paul -> pablo
            var newDir = Path.Combine(basePath, "agents", "pablo");
            Directory.Move(paulDir, newDir);

            // Update AGENT.md title
            var agentFile = Path.Combine(newDir, "AGENT.md");
            var content = File.ReadAllText(agentFile);
            content = content.Replace("# Paul", "# Pablo");
            File.WriteAllText(agentFile, content);

            // Update val's allowed_chats
            var valFile = Path.Combine(valDir, "AGENT.md");
            var valConfig = AgentLoader.Parse(File.ReadAllText(valFile))!;
            Assert.Contains("paul", valConfig.AllowChat!);
            valConfig.AllowChat = valConfig.AllowChat!.Select(c => c == "paul" ? "pablo" : c).ToList();
            File.WriteAllText(valFile, AgentLoader.Serialize("Val", valConfig));

            // Assert
            Assert.False(Directory.Exists(paulDir));
            Assert.True(Directory.Exists(newDir));
            Assert.Contains("# Pablo", File.ReadAllText(agentFile));

            var updatedValConfig = AgentLoader.Parse(File.ReadAllText(valFile))!;
            Assert.Contains("pablo", updatedValConfig.AllowChat!);
            Assert.DoesNotContain("paul", updatedValConfig.AllowChat!);
        }
        finally
        {
            if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        }
    }
}
