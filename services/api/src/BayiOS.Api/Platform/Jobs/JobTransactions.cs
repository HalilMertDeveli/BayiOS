using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// <see cref="IJobTransactions"/> of one claimed job (scoped to the job's DI scope, bound by <see cref="JobProcessor"/>).
/// Every call creates a child scope with the job's tenant, begins its UnitOfWork, runs the work and commits — the
/// connection is back in the pool when the call returns, so nothing is held between calls (ADR 0006/0010).
/// </summary>
internal sealed class JobTransactions(IServiceScopeFactory scopeFactory) : IJobTransactions
{
    private ClaimedJob? _job;
    private string? _workerId;
    private string? _correlationId;

    /// <summary>Null until <see cref="CompleteAsync"/> ran; then whether the job was marked succeeded (false = lease lost).</summary>
    public bool? Outcome { get; private set; }

    public void Bind(ClaimedJob job, string workerId, string? correlationId)
    {
        _job = job;
        _workerId = workerId;
        _correlationId = correlationId;
    }

    public async Task<T> RunAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = CreateScope(out var unitOfWork);
        await unitOfWork.BeginAsync(readOnly: false, cancellationToken);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(CancellationToken.None);
        return result;
    }

    public async Task<bool> CompleteAsync(Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Outcome is { } done)
        {
            return done;
        }

        var job = _job ?? throw new InvalidOperationException("The job transactions are not bound to a claimed job.");
        await using var scope = CreateScope(out var unitOfWork);
        await unitOfWork.BeginAsync(readOnly: false, cancellationToken);
        await work(scope.ServiceProvider, cancellationToken);
        if (!await JobStore.CompleteAsync(unitOfWork, job.Id, _workerId!, cancellationToken))
        {
            try
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
            }
            catch (NpgsqlException)
            {
                // Broken connection: disposing the scope rolls the transaction back.
            }

            Outcome = false;
            return false;
        }

        await unitOfWork.CommitAsync(CancellationToken.None);
        Outcome = true;
        return true;
    }

    private AsyncServiceScope CreateScope(out UnitOfWork unitOfWork)
    {
        var job = _job ?? throw new InvalidOperationException("The job transactions are not bound to a claimed job.");
        var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.UserId = null;
        context.Role = null;
        context.TenantId = job.TenantId;
        context.CorrelationId = _correlationId ?? job.Id.ToString("N");
        unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
        return scope;
    }
}
