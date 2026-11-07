using BayiOS.Api.Platform.Jobs;

namespace BayiOS.UnitTests.Platform;

/// <summary>#121: an enqueue wakes the idle job loop at once; without a signal the wait is the idle timeout.</summary>
public sealed class JobWakeupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signal_ends_the_wait_immediately_and_is_consumed()
    {
        using var wakeup = new JobWakeup();
        wakeup.Signal();
        wakeup.Signal(); // at most one pending signal

        Assert.True(await wakeup.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, Ct));
        Assert.False(await wakeup.WaitAsync(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct));
    }

    [Fact]
    public async Task A_signal_while_waiting_wakes_the_waiter()
    {
        using var wakeup = new JobWakeup();
        var waiting = wakeup.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, Ct);
        await Task.Delay(20, Ct);
        Assert.False(waiting.IsCompleted);

        wakeup.Signal();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Stopping_cancels_the_wait()
    {
        using var wakeup = new JobWakeup();
        using var stop = new CancellationTokenSource();
        var waiting = wakeup.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
