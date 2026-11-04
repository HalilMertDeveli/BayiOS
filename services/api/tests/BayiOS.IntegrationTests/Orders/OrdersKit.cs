using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using Npgsql;

namespace BayiOS.IntegrationTests.Orders;

/// <summary>
/// A fresh tenant with an owner, an "adet" unit, a warehouse, a default VAT-excluded sales list and a customer, plus
/// helpers for variants with prices, stock, orders and transitions over HTTP and superuser reconciliation queries.
/// Synthetic motorcycle-parts / LED data only — no real prices or customers.
/// </summary>
internal sealed class OrdersKit
{
    private readonly ApiFixture _fixture;
    private readonly Guid _unitId;
    private int _sequence;

    private OrdersKit(ApiFixture fixture, CatalogClient owner, Guid unitId, Guid warehouseId, Guid priceListId, Guid customerId)
    {
        _fixture = fixture;
        Owner = owner;
        _unitId = unitId;
        WarehouseId = warehouseId;
        PriceListId = priceListId;
        CustomerId = customerId;
    }

    public CatalogClient Owner { get; }

    public Guid TenantId => Owner.TenantId;

    public Guid WarehouseId { get; }

    public Guid PriceListId { get; }

    public Guid CustomerId { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<OrdersKit> CreateAsync(ApiFixture fixture)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");
        var unit = await owner.CreateUnitAsync("adet", "Adet");
        var warehouse = await owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "MAIN", name = "Ana Depo" });
        var list = await owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Perakende", isDefault = true });
        var customer = await owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "C0001", name = "Sentetik Motor Ltd." });
        return new OrdersKit(fixture, owner, unit.Id, warehouse.Id, list.Id, customer.Customer.Id);
    }

    /// <summary>A variant (VAT 20 %) with an optional price on the default list.</summary>
    public async Task<Guid> VariantAsync(decimal? price = null, string? sku = null, string? barcode = null)
    {
        var code = sku ?? $"LED-{Interlocked.Increment(ref _sequence):D4}";
        var product = await Owner.CreateSimpleProductAsync(_unitId, "Sentetik LED far " + code, code, barcode);
        var variantId = Assert.Single(product.Variants).Id;
        if (price is { } p)
        {
            await PriceAsync(PriceListId, variantId, p);
        }

        return variantId;
    }

    public async Task PriceAsync(Guid priceListId, Guid variantId, decimal price)
    {
        var response = await Owner.SendAsync(
            HttpMethod.Put,
            $"/api/v1/price-lists/{priceListId}/items",
            new { items = new[] { new { variantId, minQuantity = 1m, unitPrice = price } } },
            idempotencyKey: Guid.NewGuid().ToString());
        Assert.True(response.IsSuccessStatusCode, $"price: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    public async Task ReceiveAsync(Guid variantId, decimal quantity, Guid? warehouseId = null, Guid? locationId = null)
    {
        var response = await Owner.PostAsync("/api/v1/inventory/receipts", new
        {
            warehouseId = warehouseId ?? WarehouseId,
            reference = "İrsaliye TEST-1",
            lines = new[] { new { variantId, quantity, locationId } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"receipt: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    public static object Line(Guid variantId, decimal quantity, decimal? unitPrice = null, decimal? discountPercent = null, decimal? discountAmount = null, Guid? id = null) =>
        new { id, variantId, quantity, unitPrice, discountPercent, discountAmount };

    public Task<HttpResponseMessage> CreateResponseAsync(CatalogClient client, object[] lines, Guid? customerId = null, string? key = null, Guid? warehouseId = null, bool? pricesIncludeTax = null, string? source = null) =>
        client.PostAsync(
            "/api/v1/orders",
            new { customerId = customerId ?? CustomerId, warehouseId = warehouseId ?? WarehouseId, lines, pricesIncludeTax, source, notes = "Test siparişi" },
            key);

    public async Task<OrderDetailResponse> CreateOrderAsync(params object[] lines) =>
        await ReadAsync(await CreateResponseAsync(Owner, lines), HttpStatusCode.Created);

    public async Task<OrderDetailResponse> SubmittedOrderAsync(params object[] lines)
    {
        var order = await CreateOrderAsync(lines);
        return await ReadAsync(await ActAsync(Owner, order, "submit"));
    }

    public async Task<OrderDetailResponse> ApprovedOrderAsync(params object[] lines)
    {
        var order = await SubmittedOrderAsync(lines);
        return await ReadAsync(await ActAsync(Owner, order, "approve"));
    }

    /// <summary>POSTs a transition with a fresh Idempotency-Key and If-Match = the given order's version.</summary>
    public static Task<HttpResponseMessage> ActAsync(CatalogClient client, OrderDetailResponse order, string action, object? body = null, string? key = null, bool ifMatch = true) =>
        client.SendAsync(
            HttpMethod.Post,
            $"/api/v1/orders/{order.Order.Id}/{action}",
            body ?? new { },
            ifMatch ? CatalogClient.ETag(order.Order.Version) : null,
            idempotencyKey: key);

    /// <summary>
    /// Ships through the Warehouse flow as the owner: picks the given quantity per order line on the active pick task
    /// (the rest is reported short), completes the task and confirms the draft shipment. Returns the order.
    /// </summary>
    public async Task<OrderDetailResponse> ShipAsync(OrderDetailResponse order, params (Guid LineId, decimal Quantity)[] lines)
    {
        var task = await Fulfillment.ActiveTaskAsync(Owner, order.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(Owner, task, lines.ToDictionary(l => l.LineId, l => l.Quantity));
        await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(Owner, completed.ShipmentId!.Value));
        return await GetAsync(order.Order.Id);
    }

    public async Task<OrderDetailResponse> GetAsync(Guid orderId) =>
        await ReadAsync(await Owner.GetAsync($"/api/v1/orders/{orderId}"));

    public static async Task<OrderDetailResponse> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<OrderDetailResponse>(Ct))!;
    }

    /// <summary>(on_hand, reserved) of one balance row, read as superuser; (0, 0) when the row does not exist.</summary>
    public async Task<(decimal OnHand, decimal Reserved)> BalanceAsync(Guid variantId, Guid? warehouseId = null, Guid? locationId = null)
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
        command.Parameters.Add(new NpgsqlParameter { Value = warehouseId ?? WarehouseId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)locationId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetDecimal(0), reader.GetDecimal(1)) : (0m, 0m);
    }

    /// <summary>Scalar count for this tenant (<c>$1</c> = tenant id), as superuser.</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<long> ActiveReservationsAsync() =>
        CountAsync("SELECT count(*) FROM inventory.stock_reservations WHERE tenant_id = $1 AND status = 'active'");

    /// <summary>
    /// Reconciliation of orders against the stock engine for this tenant (inventory-and-orders §4.9, I1–I3, I7):
    /// ledger ⇔ balances, balances.reserved ⇔ open reservations, every order line's reserved / shipped mirror ⇔ its
    /// reservations, shipment lines and <c>shipment_out</c> movements, and nothing negative or over-reserved.
    /// </summary>
    public async Task AssertConsistentAsync()
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
            "SELECT count(*) FROM inventory.inventory_balances WHERE tenant_id = $1 AND (on_hand < 0 OR reserved > on_hand OR reserved < 0)"));

        // Order line mirrors ⇔ inventory reservations.
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM orders.order_lines l
            WHERE l.tenant_id = $1 AND l.qty_reserved <> (
                SELECT coalesce(sum(r.quantity - r.consumed_quantity - r.released_quantity), 0) FROM inventory.stock_reservations r
                WHERE r.tenant_id = l.tenant_id AND r.order_line_id = l.id AND r.status = 'active')
            """));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM orders.order_lines l
            WHERE l.tenant_id = $1 AND l.qty_shipped <> (
                SELECT coalesce(sum(r.consumed_quantity), 0) FROM inventory.stock_reservations r
                WHERE r.tenant_id = l.tenant_id AND r.order_line_id = l.id)
            """));

        // Shipped quantities ⇔ shipment lines ⇔ shipment_out movements.
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM orders.order_lines l
            WHERE l.tenant_id = $1 AND l.qty_shipped <> (
                SELECT coalesce(sum(s.quantity), 0) FROM orders.order_shipment_lines s WHERE s.tenant_id = l.tenant_id AND s.order_line_id = l.id)
            """));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM orders.order_shipment_lines s
            WHERE s.tenant_id = $1 AND -s.quantity <> (
                SELECT coalesce(sum(m.quantity), 0) FROM inventory.inventory_movements m
                WHERE m.tenant_id = s.tenant_id AND m.type = 'shipment_out' AND m.source_type = 'shipment'
                  AND m.source_id = s.shipment_id AND m.source_line_id = s.id)
            """));

        // Warehouse shipments ⇔ order-side shipments: confirmed lines are recorded with the same ids and quantities;
        // unconfirmed or cancelled shipments never issued stock.
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM warehouse.shipment_lines wl JOIN warehouse.shipments ws ON ws.tenant_id = wl.tenant_id AND ws.id = wl.shipment_id
            WHERE wl.tenant_id = $1 AND ws.status = 'confirmed' AND wl.quantity > 0 AND NOT EXISTS (
                SELECT 1 FROM orders.order_shipment_lines ol
                WHERE ol.tenant_id = wl.tenant_id AND ol.id = wl.id AND ol.shipment_id = ws.id AND ol.quantity = wl.quantity)
            """));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM orders.order_shipment_lines ol
            WHERE ol.tenant_id = $1 AND NOT EXISTS (
                SELECT 1 FROM warehouse.shipments ws WHERE ws.tenant_id = ol.tenant_id AND ws.id = ol.shipment_id AND ws.status = 'confirmed')
            """));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM inventory.inventory_movements m JOIN warehouse.shipments ws ON ws.tenant_id = m.tenant_id AND ws.id = m.source_id
            WHERE m.tenant_id = $1 AND ws.status <> 'confirmed'
            """));

        // Picking: what a task line picked never exceeds what it had to pick (CHECK), and shipments carry the picks.
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM warehouse.shipment_lines wl JOIN warehouse.shipments ws ON ws.tenant_id = wl.tenant_id AND ws.id = wl.shipment_id
            WHERE wl.tenant_id = $1 AND wl.picked_quantity <> (
                SELECT coalesce(sum(pl.qty_picked), 0) FROM warehouse.picking_task_lines pl
                WHERE pl.tenant_id = wl.tenant_id AND pl.picking_task_id = ws.picking_task_id AND pl.order_line_id = wl.order_line_id
                  AND pl.location_id IS NOT DISTINCT FROM wl.location_id)
            """));

        // I7 and terminal states hold no stock.
        Assert.Equal(0L, await CountAsync(
            "SELECT count(*) FROM orders.order_lines WHERE tenant_id = $1 AND qty_reserved + qty_shipped + qty_cancelled > quantity"));
        Assert.Equal(0L, await CountAsync(
            """
            SELECT count(*) FROM orders.order_lines l JOIN orders.orders o ON o.tenant_id = l.tenant_id AND o.id = l.order_id
            WHERE l.tenant_id = $1 AND o.status IN ('draft', 'pending_approval', 'shipped', 'completed', 'cancelled') AND l.qty_reserved <> 0
            """));
    }
}
