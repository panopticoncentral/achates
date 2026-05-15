using System.Runtime.CompilerServices;
using Achates.Agent.Events;
using Achates.Server.Cron;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achates.Tests;

/// <summary>
/// Regression tests for the cron per-job wall-clock timeout. A stalled agent run
/// must not freeze the cron loop: it should be bounded and reported as an error,
/// while genuine service shutdown still propagates.
/// </summary>
public class CronServiceTimeoutTests
{
    private static CronJob MakeJob() => new()
    {
        Id = "t1",
        Name = "Test Job",
        AgentName = "tester",
        Schedule = new CronSchedule.Every(TimeSpan.FromMinutes(5)),
        Message = "do the thing",
        Delivery = new CronDeliveryTarget(),
    };

    private static async IAsyncEnumerable<AgentEvent> NeverEndingAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    private static async IAsyncEnumerable<AgentEvent> NormalAsync()
    {
        yield return new AgentStartEvent();
        await Task.Yield();
    }

    [Fact]
    public async Task StalledStream_TimesOut_ReturnsError_DoesNotHang()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var (response, error) = await CronService.ConsumeJobStreamAsync(
            NeverEndingAsync(),
            costLedger: null,
            MakeJob(),
            timeout: TimeSpan.FromMilliseconds(200),
            NullLogger.Instance,
            serviceCt: CancellationToken.None);

        sw.Stop();

        Assert.NotNull(error);
        Assert.Contains("Timed out", error);
        Assert.Contains("time limit", response);
        // Bounded: must return well before any plausible test hang.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task ServiceShutdown_Propagates_NotTreatedAsTimeout()
    {
        using var serviceCts = new CancellationTokenSource();
        serviceCts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CronService.ConsumeJobStreamAsync(
                NeverEndingAsync(),
                costLedger: null,
                MakeJob(),
                // Long timeout so the service-shutdown cancellation wins.
                timeout: TimeSpan.FromMinutes(5),
                NullLogger.Instance,
                serviceCts.Token));
    }

    [Fact]
    public async Task NormalCompletion_ReturnsNoError()
    {
        var (response, error) = await CronService.ConsumeJobStreamAsync(
            NormalAsync(),
            costLedger: null,
            MakeJob(),
            timeout: TimeSpan.FromMinutes(5),
            NullLogger.Instance,
            serviceCt: CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("", response);
    }
}
