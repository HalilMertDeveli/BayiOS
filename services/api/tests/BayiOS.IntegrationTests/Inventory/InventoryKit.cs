using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Billing;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>
/// A fresh tenant with an owner, an "adet" unit and helpers for warehouses, variants, stock calls over HTTP and
/// direct engine calls (reserve/release/issue have no endpoint) in their own UnitOfWork. Synthetic data only.
/// </summary>
internal sealed class InventoryKit
{
    private readonly ApiFixture _fixture;
    private readonly Guid _unitId;
    private int _sku;

    private InventoryKit(ApiFixture fixture, CatalogClient owner, Guid unitId)
    {
        _fixture = fixture;
        Owner = owner;
        _unitId = unitId;
    }

    public CatalogClient Owner { get; }

    public Guid TenantId => Owner.TenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<InventoryKit> CreateAsync(ApiFixture fixture)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");

        // Explicit plan (#51): multi-warehouse and locations are Professional features, not just "the trial".
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Professional);
        var unit = await owner.CreateUnitAsync("adet", "Adet");
        return new InventoryKit(fixture, owner, unit.Id);
    }

    public async Task<Guid> VariantAsync(string? sku = null, string? barcode = null)
    {
        var code = sku ?? $"SKU-{Interlocked.Increment(ref _sku):D4}";
        var product = await Owner.CreateSimpleProductAsync(_unitId, "Sentetik parça " + code, code, barcode);
        return Assert.Single(product.Variants).Id;
    }

    public Task<WarehouseResponse> WarehouseAsync(string code, bool usesLocations = false) =>
        Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code, name = "Depo " + code, usesLocations });

    public Task<LocationResponse> LocationAsync(Guid warehouseId, string code, int pickSequence = 0) =>
        Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouseId}/locations", new { code, pickSequence });

    public async Task<ReceiptResult> ReceiveAsync(Guid? warehouseId, params (Guid VariantId, decimal Quantity, Guid? LocationId)[] lines)
    {
        var response = await Owner.PostAsync("/api/v1/inventory/receipts", new
        {
            warehouseId,
            reference = "İrsaliye TEST-1",
            lines = lines.Select(l => new { variantId = l.VariantId, quantity = l.Quantity, locationId = l.LocationId }),
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"receipt: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<ReceiptResult>(Ct))!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Reads as kit.AdjustAsync at call sites.")]
    public Task<HttpResponseMessage> AdjustAsync(
        CatalogClient client, Guid variantId, Guid warehouseId, string type, decimal quantity, bool? confirm = null, Guid? locationId = null) =>
        client.PostAsync("/api/v1/inventory/adjustments", new
        {
            variantId,
            warehouseId,
            locationId,
            type,
            quantity,
            reason = "Sayım farkı (test)",
            confirmNegativeStock = confirm,
        });

    /// <summary>Runs an engine command in its own request-like UnitOfWork; commits on success, rolls back otherwise.</summary>
    public async Task<InventoryResult<T>> EngineAsync<T>(Func<IInventoryService, Task<InventoryResult<T>>> command)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var unitOfWork = await BeginAsync(scope.ServiceProvider);
        var result = await command(scope.ServiceProvider.GetRequiredService<IInventoryService>());
        if (result.Succeeded)
        {
            await unitOfWork.CommitAsync(Ct);
        }
        else
        {
            await unitOfWork.RollbackAsync(Ct);
        }

        return result;
    }

    /// <summary>Starts a UnitOfWork bound to this tenant's owner, as the request pipeline would.</summary>
    public async Task<UnitOfWork> BeginAsync(IServiceProvider services)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.UserId = Owner.UserId;
        context.TenantId = TenantId;
        context.Role = Roles.Owner;
        var unitOfWork = services.GetRequiredService<UnitOfWork>();
        await unitOfWork.BeginAsync(readOnly: false, Ct);
        return unitOfWork;
    }

    public Task<InventoryResult<ReservationResult>> ReserveAsync(Guid warehouseId, params (Guid VariantId, decimal Quantity)[] lines) =>
        ReserveForOrderAsync(Guid.CreateVersion7(), warehouseId, lines);

    public Task<InventoryResult<ReservationResult>> ReserveForOrderAsync(Guid orderId, Guid warehouseId, params (Guid VariantId, decimal Quantity)[] lines) =>
        EngineAsync(s => s.ReserveAsync(
            new ReserveStock(orderId, warehouseId, [.. lines.Select(l => new ReserveLine(Guid.CreateVersion7(), l.VariantId, l.Quantity))]),
            Ct));

    public async Task SetNegativeStockPolicyAsync(string policy)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE identity.tenant_settings SET negative_stock_policy = $1 WHERE tenant_id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = policy });
        command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    /// <summary>(on_hand, reserved) of one balance row, read as superuser; (0, 0) when the row does not exist.</summary>
    public async Task<(decimal OnHand, decimal Reserved)> BalanceAsync(Guid variantId, Guid warehouseId, Guid? locationId = null)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT on_hand, reserved FROM inventory.inventory_balances
            WHERE tenant_id = $1 AND variant_id = $2 AND warehouse_id = $3 AND location_id IS NOT DISTINCT FROM $4
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = variantId });
        command.Parameters.Add(new NpgsqlParameter { Value = warehouseId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)locationId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetDecimal(0), reader.GetDecimal(1)) : (0m, 0m);
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// Reconciliation (inventory-and-orders §4.9, invariants I1–I3) for this tenant: every balance equals the sum of
    /// its ledger, reserved equals the open active reservations, no ledger key lacks a balance row and — unless
    /// negative stock was allowed — nothing is negative or over-reserved.
    /// </summary>
    public async Task AssertInvariantsAsync(bool negativeAllowed = false)
    {
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM inventory.inventory_balances b
            WHERE b.tenant_id = $1 AND b.on_hand <> (
                SELECT coalesce(sum(m.quantity), 0) FROM inventory.inventory_movements m
                WHERE m.tenant_id = b.tenant_id AND m.variant_id = b.variant_id AND m.warehouse_id = b.warehouse_id
                  AND m.location_id IS NOT DISTINCT FROM b.location_id)
            """));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM inventory.inventory_balances b
            WHERE b.tenant_id = $1 AND b.reserved <> (
                SELECT coalesce(sum(r.quantity - r.consumed_quantity - r.released_quantity), 0) FROM inventory.stock_reservations r
                WHERE r.tenant_id = b.tenant_id AND r.status = 'active' AND r.variant_id = b.variant_id
                  AND r.warehouse_id = b.warehouse_id AND r.location_id IS NOT DISTINCT FROM b.location_id)
            """));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM inventory.inventory_movements m
            WHERE m.tenant_id = $1 AND NOT EXISTS (
                SELECT 1 FROM inventory.inventory_balances b
                WHERE b.tenant_id = m.tenant_id AND b.variant_id = m.variant_id AND b.warehouse_id = m.warehouse_id
                  AND b.location_id IS NOT DISTINCT FROM m.location_id)
            """));
        if (!negativeAllowed)
        {
            Assert.Equal(0L, await CountAsync(
                "SELECT count(*) FROM inventory.inventory_balances WHERE tenant_id = $1 AND (on_hand < 0 OR reserved > on_hand OR reserved < 0)"));
        }
    }
}
