using System.Data;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.SharedKernel.Persistence;

/// <summary>
/// One connection + one transaction per request, shared by every module DbContext (system-architecture §5.3).
/// At transaction start it sets the transaction-local <c>app.user_id</c> / <c>app.tenant_id</c> settings that the
/// PostgreSQL RLS policies read (ADR 0002). Settings never outlive the transaction, so pooled connections stay clean.
/// </summary>
public sealed class UnitOfWork(NpgsqlDataSource dataSource, RequestContext requestContext) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _completed;
    private List<Action>? _onCommitted;

    /// <summary>The shared connection. Module DbContexts are configured with it.</summary>
    public NpgsqlConnection Connection => _connection ??= dataSource.CreateConnection();

    /// <summary>The active transaction, or null before <see cref="BeginAsync"/> / after completion.</summary>
    public NpgsqlTransaction? Transaction => _completed ? null : _transaction;

    public bool IsActive => Transaction is not null;

    /// <summary>The transaction was started with <c>SET TRANSACTION READ ONLY</c> (GET/HEAD requests).</summary>
    public bool IsReadOnly { get; private set; }

    public async Task BeginAsync(bool readOnly, CancellationToken cancellationToken)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The unit of work has already completed.");
        }

        if (_transaction is not null)
        {
            return;
        }

        if (Connection.State != ConnectionState.Open)
        {
            await Connection.OpenAsync(cancellationToken);
        }

        _transaction = await Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        IsReadOnly = readOnly;

        await using var batch = new NpgsqlBatch(Connection, _transaction);
        if (readOnly)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand("SET TRANSACTION READ ONLY"));
        }

        var settings = new NpgsqlBatchCommand(
            "SELECT set_config('app.user_id', $1, true), set_config('app.tenant_id', $2, true)");
        settings.Parameters.Add(TextParameter(requestContext.UserId));
        settings.Parameters.Add(TextParameter(requestContext.TenantId));
        batch.BatchCommands.Add(settings);

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Switches the transaction-local tenant (e.g. after membership check or when creating a tenant).</summary>
    public async Task SetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var transaction = Transaction ?? throw new InvalidOperationException("The unit of work has not started.");

        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", Connection, transaction);
        command.Parameters.Add(TextParameter(tenantId));
        await command.ExecuteNonQueryAsync(cancellationToken);

        requestContext.TenantId = tenantId;
    }

    /// <summary>
    /// Registers a side-effect-free callback (e.g. a metric increment) that runs only after this unit of work
    /// commits; a rollback discards it. Callbacks must not throw or touch the database.
    /// </summary>
    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        (_onCommitted ??= []).Add(callback);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (Transaction is { } transaction)
        {
            await transaction.CommitAsync(cancellationToken);
            _completed = true;
            if (_onCommitted is { } callbacks)
            {
                _onCommitted = null;
                foreach (var callback in callbacks)
                {
                    callback();
                }
            }
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (Transaction is { } transaction)
        {
            await transaction.RollbackAsync(cancellationToken);
            _completed = true;
            _onCommitted = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            // Disposing an uncommitted NpgsqlTransaction rolls it back.
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private static NpgsqlParameter<string> TextParameter(Guid? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, TypedValue = value?.ToString() ?? string.Empty };
}
