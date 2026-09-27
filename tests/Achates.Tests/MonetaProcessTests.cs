using System.Diagnostics;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class MonetaProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moneta process {Guid.NewGuid():N}");

    private string Script(string body)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Arguments_are_literal_and_stdin_is_closed()
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = Script("test \"$1\" = --database || exit 9\ntest \"$2\" = '/tmp/a b;$(false).db' || exit 9\ncat >/dev/null\nprintf '%s' '{\"protocol_version\":1,\"rows\":[]}'\n");
        var result = await MonetaProcess.RunAsync(executable, "/tmp/a b;$(false).db", "{}", default);
        Assert.Contains("rows", result);
    }

    [Theory]
    [InlineData("printf 'invalid JSON'")]
    [InlineData("printf '%s' '{\"protocol_version\":2}'")]
    [InlineData("printf '%s' '{\"protocol_version\":1}'; exit 1")]
    public async Task Malformed_responses_fail(string body)
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAnyAsync<Exception>(() => MonetaProcess.RunAsync(Script(body), "/tmp/synthetic.db", "{}", default));
    }

    [Fact]
    public async Task Structured_cli_errors_are_preserved()
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = Script("printf '%s' '{\"protocol_version\":1,\"error\":{\"code\":\"missing_budget_year\"}}'; exit 1");
        var result = await MonetaProcess.RunAsync(executable, "/tmp/synthetic.db", "{}", default);
        Assert.Contains("missing_budget_year", result);
    }

    [Fact]
    public async Task Timeout_kills_process_and_returns_promptly()
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = Script("exec sleep 30");
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MonetaProcess.RunAsync(executable, "/tmp/synthetic.db", "{}", default, TimeSpan.FromMilliseconds(100)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" >&2")]
    public async Task Excess_output_is_bounded_and_process_stopped(string destination)
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = Script("cat >/dev/null\nyes x" + destination);
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            MonetaProcess.RunAsync(executable, "/tmp/synthetic.db", "{}", default, TimeSpan.FromSeconds(5)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
