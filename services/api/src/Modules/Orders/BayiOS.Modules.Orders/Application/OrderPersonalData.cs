using System.Data.Common;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BayiOS.Modules.Orders.Application;

/// <summary>
/// KVKK erasure of order address snapshots (#115). The masking rule lives in the database
/// (<c>orders.anonymize_address_snapshot</c>, data-model §5.8): the post-approval immutability trigger accepts exactly
/// that value, and only while the transaction-local marker <see cref="MarkerSetting"/> names the order's customer
/// (and <c>app.tenant_id</c> its tenant). The marker is cleared again in the same command, right after the update.
/// </summary>
internal sealed class OrderPersonalData(OrdersDbContext db, IRequestContext requestContext, TimeProvider clock) : IOrderPersonalData
{
    public const string MarkerSetting = "app.anonymize_customer_id";

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    public async Task<int> AnonymizeCustomerSnapshotsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);

        // Lock hierarchy step 1 (order headers, by id) for every order of the customer — drafts without a snapshot
        // included — so a submit / approve waiting on one of them re-reads the (by then removed) customer addresses.
        await using (var lockOrders = Command(
            """
            SET LOCAL lock_timeout = '5s';
            SELECT count(*) FROM (
                SELECT 1 FROM orders.orders WHERE tenant_id = @tenant AND customer_id = @customer ORDER BY id FOR UPDATE) locked;
            """))
        {
            lockOrders.Parameters.Add(new NpgsqlParameter<Guid>("tenant", TenantId));
            lockOrders.Parameters.Add(new NpgsqlParameter<Guid>("customer", customerId));
            await lockOrders.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var mask = Command(
            $"""
            SELECT set_config('{MarkerSetting}', @customer::text, true);
            WITH masked AS (
                UPDATE orders.orders
                SET shipping_address = orders.anonymize_address_snapshot(shipping_address),
                    billing_address = orders.anonymize_address_snapshot(billing_address),
                    row_version = row_version + 1,
                    updated_at = @now,
                    updated_by = @user
                WHERE tenant_id = @tenant AND customer_id = @customer
                  AND (shipping_address IS DISTINCT FROM orders.anonymize_address_snapshot(shipping_address)
                       OR billing_address IS DISTINCT FROM orders.anonymize_address_snapshot(billing_address))
                RETURNING 1)
            SELECT count(*)::int FROM masked;
            SELECT set_config('{MarkerSetting}', '', true);
            """);
        mask.Parameters.Add(new NpgsqlParameter<Guid>("tenant", TenantId));
        mask.Parameters.Add(new NpgsqlParameter<Guid>("customer", customerId));
        mask.Parameters.Add(new NpgsqlParameter<Guid>("user", UserId));
        mask.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", clock.GetUtcNow()));

        int count;
        await using (var reader = await mask.ExecuteReaderAsync(cancellationToken))
        {
            await reader.NextResultAsync(cancellationToken); // past the marker set_config
            await reader.ReadAsync(cancellationToken);
            count = reader.GetInt32(0);
            while (await reader.NextResultAsync(cancellationToken))
            {
                // Consume the marker reset so it is guaranteed to have run.
            }
        }

        return count;
    }

    private DbCommand Command(string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }
}
