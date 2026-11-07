namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// In-process wake-up of the job loop (ADR 0006 §3, polling stays the source of truth): <see cref="BackgroundJobs"/>
/// signals it after the enqueuing transaction commits, so a job submitted to this instance is claimed right away
/// instead of after the idle backoff (up to <c>Jobs:MaxIdleInterval</c>). At most one pending signal is kept; a signal
/// that arrives while the loop is working makes the next wait return immediately (one extra, harmless poll).
/// </summary>
internal sealed class JobWakeup : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    /// <summary>Waits for a signal or the timeout; true when woken by a signal.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalled = _signal.WaitAsync(linked.Token);
        var delay = Task.Delay(timeout, clock, linked.Token);
        var first = await Task.WhenAny(signalled, delay);
        await linked.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();

        // The signal may have been consumed even if the delay won the race: report it so the backoff resets.
        try
        {
            await signalled;
            return true;
        }
        catch (OperationCanceledException)
        {
            return first == signalled;
        }
    }

    public void Dispose() => _signal.Dispose();
}
