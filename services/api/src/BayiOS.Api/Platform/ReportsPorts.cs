using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Integrations.Infrastructure.Persistence;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using BayiOS.Modules.Orders.Domain;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Reports ports (same approach as <see cref="IntegrationsPorts"/>): each one reads
/// only the schema of the module that owns the data, through that module's DbContext on the request connection and
/// transaction — RLS (<c>app.tenant_id</c>) applies, and every query also filters <c>tenant_id</c> explicitly.
/// Lookups use the modules' EF models (global tenant query filters); per-day aggregates are single grouped SQL
/// statements over one module's tables (Europe/Istanbul day = UTC + 3 h, no DST since 2016). Read-only: nothing here
/// writes, and no statement joins tables of two modules.
/// </summary>
internal static class ReportsPorts
{
    public static IServiceCollection AddReportsPorts(this IServiceCollection services)
    {
        services.AddScoped<IReportCatalogSource, CatalogReportSource>();
        services.AddScoped<IReportInventorySource, InventoryReportSource>();
        services.AddScoped<IReportOrderSource, OrdersReportSource>();
        services.AddScoped<IReportWarehouseSource, WarehouseReportSource>();
        services.AddScoped<IReportAiUsageSource, AiUsageReportSource>();
        return services;
    }
}

/// <summary>Active variants of active products by SKU (keyset), with unit code. No prices.</summary>
internal sealed class CatalogReportSource(CatalogDbContext catalog) : IReportCatalogSource
{
    private const string Active = "active";

    public async Task<IReadOnlyList<ReportVariant>> ActiveVariantsAsync(
        IReadOnlyCollection<Guid> excluded, string? afterSku, int limit, CancellationToken cancellationToken)
    {
        var ids = excluded.ToList();
        var rows = await (
                from variant in catalog.Variants.AsNoTracking()
                join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
                join unit in catalog.Units.AsNoTracking() on variant.UnitId equals unit.Id
                where variant.Status == Active && product.Status == Active
                      && !ids.Contains(variant.Id)
                      && (afterSku == null || variant.Sku.CompareTo(afterSku) > 0)
                orderby variant.Sku
                select new { variant.Id, variant.ProductId, variant.Sku, product.Name, VariantName = variant.Name, Unit = unit.Code })
            .Take(limit)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ReportVariant(
            r.Id, r.ProductId, r.Sku, string.IsNullOrWhiteSpace(r.VariantName) ? r.Name : $"{r.Name} {r.VariantName}", r.Unit))];
    }

    public async Task<IReadOnlyList<ReportVariant>> ActiveVariantsInAsync(
        IReadOnlyCollection<Guid> included, string? afterSku, int limit, CancellationToken cancellationToken)
    {
        var ids = included.ToList();
        var rows = await (
                from variant in catalog.Variants.AsNoTracking()
                join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
                join unit in catalog.Units.AsNoTracking() on variant.UnitId equals unit.Id
                where variant.Status == Active && product.Status == Active
                      && ids.Contains(variant.Id)
                      && (afterSku == null || variant.Sku.CompareTo(afterSku) > 0)
                orderby variant.Sku
                select new { variant.Id, variant.ProductId, variant.Sku, product.Name, VariantName = variant.Name, Unit = unit.Code })
            .Take(limit)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ReportVariant(
            r.Id, r.ProductId, r.Sku, string.IsNullOrWhiteSpace(r.VariantName) ? r.Name : $"{r.Name} {r.VariantName}", r.Unit))];
    }
}

/// <summary>
/// Balances (derived from the ledger) and the movement ledger. Never unit costs. Minimum stock thresholds are read
/// through Inventory's public contract <see cref="IStockThresholdQueries"/> (the effective-minimum rule is Inventory's).
/// </summary>
internal sealed class InventoryReportSource(
    InventoryDbContext inventory, UnitOfWork unitOfWork, IRequestContext requestContext, IStockThresholdQueries thresholds) : IReportInventorySource
{
    public Task<IReadOnlyDictionary<Guid, decimal>> MinimumsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken) =>
        thresholds.EffectiveMinimumsAsync(variantIds, warehouseId, cancellationToken);

    public Task<IReadOnlyList<Guid>> VariantsAtOrBelowMinimumAsync(Guid? warehouseId, CancellationToken cancellationToken) =>
        thresholds.CriticalVariantsAsync(warehouseId, cancellationToken);

    public Task<IReadOnlyList<Guid>> VariantsAboveMinimumOrZeroAsync(Guid? warehouseId, CancellationToken cancellationToken) =>
        thresholds.VariantsAboveMinimumOrZeroAsync(warehouseId, cancellationToken);

    public Task<bool> AnyMinimumAsync(Guid? warehouseId, CancellationToken cancellationToken) =>
        thresholds.AnyThresholdAsync(warehouseId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> VariantsAboveAsync(decimal threshold, Guid? warehouseId, CancellationToken cancellationToken) =>
        await inventory.Balances.AsNoTracking()
            .Where(b => !b.IsTransit && (warehouseId == null || b.WarehouseId == warehouseId))
            .GroupBy(b => b.VariantId)
            .Where(g => g.Sum(b => b.OnHand) - g.Sum(b => b.Reserved) > threshold)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ReportStockLevel>> LevelsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var ids = variantIds.ToList();
        return await inventory.Balances.AsNoTracking()
            .Where(b => !b.IsTransit && ids.Contains(b.VariantId) && (warehouseId == null || b.WarehouseId == warehouseId))
            .GroupBy(b => b.VariantId)
            .Select(g => new ReportStockLevel(g.Key, g.Sum(b => b.OnHand), g.Sum(b => b.Reserved)))
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ReportMovementDay>> MovementsByDayAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? warehouseId, CancellationToken cancellationToken) =>
        ReportSql.QueryAsync(
            inventory,
            unitOfWork,
            $"""
            SELECT {ReportSql.Day("m.occurred_at")} AS day, m.type,
                   count(*) AS movements,
                   coalesce(sum(m.quantity) FILTER (WHERE m.quantity > 0), 0) AS quantity_in,
                   coalesce(-sum(m.quantity) FILTER (WHERE m.quantity < 0), 0) AS quantity_out
            FROM inventory.inventory_movements m
            WHERE m.tenant_id = $1 AND m.occurred_at >= $2 AND m.occurred_at < $3 AND ($4::uuid IS NULL OR m.warehouse_id = $4)
              -- In-transit legs (OD-01: warehouse slot = the transfer id) would count every transfer twice; a write-off stays.
              AND NOT (m.source_type = 'transfer' AND m.warehouse_id = m.source_id AND m.type IN ('transfer_out', 'transfer_in'))
            GROUP BY 1, 2
            """,
            [ReportSql.Tenant(requestContext), ReportSql.Instant(startUtc), ReportSql.Instant(endUtc), ReportSql.Uuid(warehouseId)],
            r => new ReportMovementDay(r.GetFieldValue<DateOnly>(0), r.GetString(1), r.GetInt64(2), r.GetDecimal(3), r.GetDecimal(4)),
            cancellationToken);
}

/// <summary>Order headers and lines. No customer, address or person data.</summary>
internal sealed class OrdersReportSource(OrdersDbContext orders, UnitOfWork unitOfWork, IRequestContext requestContext) : IReportOrderSource
{
    private static readonly string[] Committed =
        [OrderStatuses.Approved, OrderStatuses.Picking, OrderStatuses.Packed, OrderStatuses.Shipped, OrderStatuses.Completed];

    public IReadOnlyList<string> Statuses => OrderStatuses.All;

    public IReadOnlyList<string> CommittedStatuses => Committed;

    public Task<IReadOnlyList<ReportOrderDay>> OrdersByDayAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<string> statuses, CancellationToken cancellationToken) =>
        ReportSql.QueryAsync(
            orders,
            unitOfWork,
            $"""
            SELECT {ReportSql.Day("o.created_at")} AS day, o.status, o.currency, count(*) AS orders, coalesce(sum(o.grand_total), 0) AS grand_total
            FROM orders.orders o
            WHERE o.tenant_id = $1 AND o.created_at >= $2 AND o.created_at < $3 AND (cardinality($4::text[]) = 0 OR o.status = ANY ($4))
            GROUP BY 1, 2, 3
            """,
            [ReportSql.Tenant(requestContext), ReportSql.Instant(startUtc), ReportSql.Instant(endUtc), ReportSql.Texts(statuses)],
            r => new ReportOrderDay(r.GetFieldValue<DateOnly>(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetDecimal(4)),
            cancellationToken);

    public Task<IReadOnlyList<ReportTopProduct>> TopProductsAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, int limit, CancellationToken cancellationToken) =>
        ReportSql.QueryAsync(
            orders,
            unitOfWork,
            """
            SELECT l.variant_id, max(l.sku) AS sku, max(l.name) AS name, max(l.unit) AS unit, o.currency,
                   sum(l.quantity) AS quantity, count(DISTINCT o.id) AS orders, sum(l.line_total) AS revenue
            FROM orders.order_lines l
            JOIN orders.orders o ON o.tenant_id = l.tenant_id AND o.id = l.order_id
            WHERE l.tenant_id = $1 AND o.tenant_id = $1 AND o.created_at >= $2 AND o.created_at < $3 AND o.status = ANY ($4)
            GROUP BY l.variant_id, o.currency
            ORDER BY quantity DESC, orders DESC, sku
            LIMIT $5
            """,
            [ReportSql.Tenant(requestContext), ReportSql.Instant(startUtc), ReportSql.Instant(endUtc), ReportSql.Texts(Committed), new NpgsqlParameter { Value = limit }],
            r => new ReportTopProduct(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetDecimal(7)),
            cancellationToken);
}

/// <summary>Warehouses and pick tasks. No assignee or other person data.</summary>
internal sealed class WarehouseReportSource(WarehouseDbContext warehouse, UnitOfWork unitOfWork, IRequestContext requestContext) : IReportWarehouseSource
{
    public Task<bool> WarehouseExistsAsync(Guid warehouseId, CancellationToken cancellationToken) =>
        warehouse.Warehouses.AsNoTracking().AnyAsync(w => w.Id == warehouseId, cancellationToken);

    /// <summary>Each task counts on the day of its own event: created, completed (any completion), cancelled.</summary>
    public Task<IReadOnlyList<ReportPickingDay>> PickingByDayAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? warehouseId, CancellationToken cancellationToken) =>
        ReportSql.QueryAsync(
            warehouse,
            unitOfWork,
            $"""
            WITH t AS (
                SELECT created_at, started_at, completed_at, cancelled_at, status
                FROM warehouse.picking_tasks
                WHERE tenant_id = $1 AND type = '{PickingTaskTypes.Pick}' AND ($4::uuid IS NULL OR warehouse_id = $4)
            )
            SELECT day, sum(created)::bigint, sum(completed)::bigint, sum(short)::bigint, sum(cancelled)::bigint, coalesce(sum(minutes), 0)
            FROM (
                SELECT {ReportSql.Day("created_at")} AS day, 1 AS created, 0 AS completed, 0 AS short, 0 AS cancelled, 0::numeric AS minutes
                FROM t WHERE created_at >= $2 AND created_at < $3
                UNION ALL
                SELECT {ReportSql.Day("completed_at")}, 0, 1, CASE WHEN status = '{PickingTaskStatuses.CompletedWithShortage}' THEN 1 ELSE 0 END, 0,
                       extract(epoch FROM completed_at - coalesce(started_at, created_at)) / 60
                FROM t
                WHERE status IN ('{PickingTaskStatuses.Completed}', '{PickingTaskStatuses.CompletedWithShortage}')
                  AND completed_at >= $2 AND completed_at < $3
                UNION ALL
                SELECT {ReportSql.Day("cancelled_at")}, 0, 0, 0, 1, 0
                FROM t WHERE status = '{PickingTaskStatuses.Cancelled}' AND cancelled_at >= $2 AND cancelled_at < $3
            ) events
            GROUP BY day
            """,
            [ReportSql.Tenant(requestContext), ReportSql.Instant(startUtc), ReportSql.Instant(endUtc), ReportSql.Uuid(warehouseId)],
            r => new ReportPickingDay(r.GetFieldValue<DateOnly>(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetDecimal(5)),
            cancellationToken);
}

/// <summary>Hourly AI usage counters summed per day. Counters only (no message text exists in this table).</summary>
internal sealed class AiUsageReportSource(IntegrationsDbContext integrations, UnitOfWork unitOfWork, IRequestContext requestContext) : IReportAiUsageSource
{
    public Task<IReadOnlyList<ReportAiUsageDay>> UsageByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken) =>
        ReportSql.QueryAsync(
            integrations,
            unitOfWork,
            $"""
            SELECT {ReportSql.Day("u.usage_hour")} AS day,
                   sum(u.parse_requests)::bigint, sum(u.ai_calls)::bigint, sum(u.ai_failures)::bigint, sum(u.fallbacks)::bigint,
                   sum(u.input_tokens)::bigint, sum(u.output_tokens)::bigint, sum(u.cache_read_tokens)::bigint, sum(u.cache_write_tokens)::bigint
            FROM integrations.ai_usage u
            WHERE u.tenant_id = $1 AND u.usage_hour >= $2 AND u.usage_hour < $3
            GROUP BY 1
            """,
            [ReportSql.Tenant(requestContext), ReportSql.Instant(startUtc), ReportSql.Instant(endUtc)],
            r => new ReportAiUsageDay(
                r.GetFieldValue<DateOnly>(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)),
            cancellationToken);
}

/// <summary>Parameterised read-only statements on a module DbContext's connection, inside the request transaction.</summary>
internal static class ReportSql
{
    /// <summary>The Europe/Istanbul calendar day of a timestamptz column (fixed UTC+03:00).</summary>
    public static string Day(string column) => $"(({column} AT TIME ZONE 'UTC') + interval '3 hours')::date";

    public static NpgsqlParameter Tenant(IRequestContext requestContext) =>
        new() { Value = requestContext.TenantId ?? throw new InvalidOperationException("Reports need a tenant."), NpgsqlDbType = NpgsqlDbType.Uuid };

    public static NpgsqlParameter Instant(DateTimeOffset value) => new() { Value = value.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public static NpgsqlParameter Uuid(Guid? value) => new() { Value = value is { } id ? id : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

    public static NpgsqlParameter Texts(IReadOnlyList<string> values) => new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        DbContext module,
        UnitOfWork unitOfWork,
        string sql,
        NpgsqlParameter[] parameters,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)module.Database.GetDbConnection();
        var transaction = unitOfWork.Transaction ?? throw new InvalidOperationException("Reports run inside the request unit of work.");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
