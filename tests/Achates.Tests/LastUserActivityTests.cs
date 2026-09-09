using Achates.Agent.Messages;
using Achates.Server.Mobile;

namespace Achates.Tests;

/// <summary>
/// Scheduled jobs see only their own channel. These pin down the cross-agent signal that
/// lets them tell "not talking to me" from "not here at all".
/// </summary>
public class LastUserActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"achates-act-{Guid.NewGuid():N}");

    private MobileSessionStore Store() => new(_root);

    private async Task SeedAsync(string agent, string sessionId, params AgentMessage[] messages)
    {
        var store = Store();
        var session = await store.CreateAsync(agent);
        session.Id = sessionId;
        session.Messages.AddRange(messages);
        await store.SaveAsync(agent, session);
    }

    private static UserMessage User(string text, DateTimeOffset at, bool hidden = false) =>
        new() { Text = text, Hidden = hidden, Timestamp = at.ToUnixTimeMilliseconds() };

    [Fact]
    public async Task Finds_the_most_recent_message_across_agents()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-6);
        var newer = DateTimeOffset.UtcNow.AddDays(-2);
        await SeedAsync("claire", "s1", User("morning", older));
        await SeedAsync("val", "s2", User("146/91", newer));

        var last = await Store().LastUserActivityAsync(["claire", "val"]);

        Assert.NotNull(last);
        Assert.Equal(newer.ToUnixTimeMilliseconds(), last!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Ignores_hidden_scheduled_prompts()
    {
        var real = DateTimeOffset.UtcNow.AddDays(-6);
        var cron = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedAsync("sasha", "s3",
            User("hey", real),
            User("[Scheduled task: Paul bedtime check-in]", cron, hidden: true));

        var last = await Store().LastUserActivityAsync(["sasha"]);

        // The scheduled prompt is the agent talking to itself; counting it would make a
        // silent week look like constant activity.
        Assert.Equal(real.ToUnixTimeMilliseconds(), last!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Returns_null_when_only_scheduled_prompts_exist()
    {
        await SeedAsync("vera", "s4", User("[Scheduled task: Dreamtime]", DateTimeOffset.UtcNow, hidden: true));

        Assert.Null(await Store().LastUserActivityAsync(["vera"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
