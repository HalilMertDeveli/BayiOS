using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// #98 over HTTP (owner decisions 2026-11-09): a pick from another shelf re-allocates the reservation, a deactivated
/// shelf hands its open task quantity to active shelves, warehouse_staff relocate shelf-to-shelf (reserved stock
/// carried along with its reservation and task lines), task priority and customer name, and the
/// <c>auto_create_picking_task</c> setting. Every scenario ends with the ledger ⇔ balance ⇔ reservation ⇔ order
/// reconciliation. Synthetic data only.
/// </summary>
public class ReallocationApiTests(ApiFixture fixture)
{
    // ---- §4.5: picked from another shelf ----

    [Fact]
    public async Task A_pick_from_another_shelf_with_free_stock_reallocates_reservation_and_task_line()
    {
        var (kit, shelves) = await LocatedAsync();
        var lamp = await kit.VariantAsync(30m, "LAMBA-01", "8690000001001");
        await kit.ReceiveAsync(lamp, 3m, shelves.WarehouseId, shelves.A.Id);
        await kit.ReceiveAsync(lamp, 4m, shelves.WarehouseId, shelves.B.Id);
        var approved = await ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(lamp, 3m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Equal(["A-01"], task.Lines.Select(l => l.LocationCode));
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, task.Task.Id, staff.UserId));
        var id = task.Task.Id;

        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, id, "LAMBA-01", 1m, "A-01"));

        // An empty shelf and an inactive one cannot take the reservation: rejected as before, nothing changes.
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "LAMBA-01", 1m, "C-01"), HttpStatusCode.UnprocessableEntity, "picking.wrong_location");
        await DeactivateAsync(kit, shelves.D);
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "LAMBA-01", 1m, "D-01"), HttpStatusCode.UnprocessableEntity, "picking.wrong_location");
        Assert.Equal((3m, 3m), await kit.BalanceAsync(lamp, shelves.WarehouseId, shelves.A.Id));

        // B-01 has 4 free: the remaining 2 move there (reservation + line), then they are picked in the same scan.
        var scan = await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, id, "8690000001001", 2m, "B-01"));
        Assert.Equal((0, 2m), (scan.OpenLineCount, Assert.Single(scan.Lines).QuantityPicked));
        Assert.Equal(shelves.B.Id, scan.Lines[0].LocationId);

        var after = await Fulfillment.TaskAsync(staff, id);
        Assert.Equal(
            [("A-01", 1m, 1m), ("B-01", 2m, 2m)],
            after.Lines.Select(l => (l.LocationCode!, l.QuantityToPick, l.QuantityPicked)));
        Assert.Equal((3m, 1m), await kit.BalanceAsync(lamp, shelves.WarehouseId, shelves.A.Id));
        Assert.Equal((4m, 2m), await kit.BalanceAsync(lamp, shelves.WarehouseId, shelves.B.Id));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.InventoryReservationReallocated}'"));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.PickingTaskReallocated}'"));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.picking_task_reallocated.v1'"));
        await kit.AssertConsistentAsync();

        // Shipping issues from both shelves exactly as re-allocated.
        var completed = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.CompleteAsync(staff, id));
        await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(staff, completed.ShipmentId!.Value));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(lamp, shelves.WarehouseId, shelves.A.Id));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(lamp, shelves.WarehouseId, shelves.B.Id));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Picking_everything_from_the_other_shelf_merges_into_one_line_and_empties_the_old_one()
    {
        var (kit, shelves) = await LocatedAsync();
        var helmet = await kit.VariantAsync(100m, "KASK-01");
        await kit.ReceiveAsync(helmet, 2m, shelves.WarehouseId, shelves.A.Id);
        await kit.ReceiveAsync(helmet, 3m, shelves.WarehouseId, shelves.B.Id);
        var approved = await ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(helmet, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);

        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, "KASK-01", 1m, "B-01"));
        var split = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
        Assert.Equal([("A-01", 1m, 0m), ("B-01", 1m, 1m)], split.Lines.Select(l => (l.LocationCode!, l.QuantityToPick, l.QuantityPicked)));

        // The second unit also comes from B: A's line is emptied (kept with nothing to pick, never deleted) and hidden.
        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, "KASK-01", 1m, "B-01"));
        var merged = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
        Assert.Equal(("B-01", 2m, 2m), (Assert.Single(merged.Lines).LocationCode!, merged.Lines[0].QuantityToPick, merged.Lines[0].QuantityPicked));
        Assert.Equal(1, merged.Task.LineCount);
        Assert.Equal(2L, await kit.CountAsync($"SELECT count(*) FROM warehouse.picking_task_lines WHERE tenant_id = $1 AND picking_task_id = '{task.Task.Id}'"));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(helmet, shelves.WarehouseId, shelves.A.Id));
        Assert.Equal((3m, 2m), await kit.BalanceAsync(helmet, shelves.WarehouseId, shelves.B.Id));

        var completed = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.CompleteAsync(kit.Owner, task.Task.Id));
        var shipment = await Fulfillment.ReadAsync<ShipmentResponse>(await kit.Owner.GetAsync($"/api/v1/shipments/{completed.ShipmentId}"));
        Assert.Equal(("B-01", 2m), (Assert.Single(shipment.Lines).LocationCode!, shipment.Lines[0].Quantity));
        await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(kit.Owner, completed.ShipmentId!.Value));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(helmet, shelves.WarehouseId, shelves.B.Id));
        await kit.AssertConsistentAsync();
    }

    // ---- The reserved shelf becomes unavailable ----

    [Fact]
    public async Task Deactivating_a_shelf_moves_open_task_quantity_to_active_shelves_and_a_supervisor_moves_the_rest()
    {
        var (kit, shelves) = await LocatedAsync();
        var tire = await kit.VariantAsync(100m, "LASTIK-01");
        await kit.ReceiveAsync(tire, 4m, shelves.WarehouseId, shelves.A.Id);
        await kit.ReceiveAsync(tire, 2m, shelves.WarehouseId, shelves.B.Id);
        var approved = await ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(tire, 4m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, "LASTIK-01", 1m, "A-01"));

        await DeactivateAsync(kit, shelves.A);

        // 3 open on A: B has 2 free → 2 move; 1 stays on the inactive shelf (flagged), the picked 1 never moves.
        var moved = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
        Assert.Equal(
            [("A-01", 2m, 1m, (bool?)false), ("B-01", 2m, 0m, (bool?)true)],
            moved.Lines.Select(l => (l.LocationCode!, l.QuantityToPick, l.QuantityPicked, l.LocationIsActive)));
        Assert.Equal((4m, 2m), await kit.BalanceAsync(tire, shelves.WarehouseId, shelves.A.Id));
        Assert.Equal((2m, 2m), await kit.BalanceAsync(tire, shelves.WarehouseId, shelves.B.Id));
        await kit.AssertConsistentAsync();

        // Stock arrives on C-01; the supervisor re-allocates what is still on the inactive shelf.
        await kit.ReceiveAsync(tire, 5m, shelves.WarehouseId, shelves.C.Id);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await HttpAssert.ProblemAsync(await Fulfillment.PostAsync(staff, $"/api/v1/picking-tasks/{task.Task.Id}/reallocate"), HttpStatusCode.Forbidden, "auth.forbidden");
        var result = await Fulfillment.ReadAsync<ReallocatePickingTaskResponse>(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/picking-tasks/{task.Task.Id}/reallocate"));
        Assert.Equal((1m, 0m), (result.MovedQuantity, result.UnmovedQuantity));
        Assert.Equal(
            [("A-01", 1m, 1m), ("B-01", 2m, 0m), ("C-01", 1m, 0m)],
            result.Task.Lines.Select(l => (l.LocationCode!, l.QuantityToPick, l.QuantityPicked)));
        Assert.Equal((5m, 1m), await kit.BalanceAsync(tire, shelves.WarehouseId, shelves.C.Id));

        // Nothing left on inactive shelves: a second call is a no-op; a foreign shelf id is refused.
        var again = await Fulfillment.ReadAsync<ReallocatePickingTaskResponse>(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/picking-tasks/{task.Task.Id}/reallocate"));
        Assert.Equal((0m, 0m), (again.MovedQuantity, again.UnmovedQuantity));
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/picking-tasks/{task.Task.Id}/reallocate", new { fromLocationId = Guid.NewGuid() }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");

        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, result.Task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 3m });
        Assert.Equal("completed", completed.Task.Status);
        await kit.AssertConsistentAsync();
    }

    // ---- Shelf-to-shelf relocation by warehouse_staff, carrying reservations ----

    [Fact]
    public async Task Warehouse_staff_relocate_shelves_and_reserved_goods_carry_their_reservation_and_task_line()
    {
        var (kit, shelves) = await LocatedAsync();
        var chain = await kit.VariantAsync(5m, "ZINCIR-01");
        await kit.ReceiveAsync(chain, 5m, shelves.WarehouseId, shelves.A.Id);
        var approved = await ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(chain, 3m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, task.Task.Id, staff.UserId));
        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, task.Task.Id, "ZINCIR-01", 1m, "A-01"));

        object Body(decimal quantity, bool? includeReserved) => new
        {
            warehouseId = shelves.WarehouseId,
            reference = "Raf düzeni",
            lines = new[] { new { variantId = chain, fromLocationId = shelves.A.Id, toLocationId = shelves.B.Id, quantity, includeReserved } },
        };

        // Available on A = 2; reserved still on the shelf = 3 − 1 picked = 2. The picked unit never moves.
        await HttpAssert.ProblemAsync(await staff.PostAsync("/api/v1/inventory/relocations", Body(3m, null)), HttpStatusCode.Conflict, "inventory.insufficient_available");
        await HttpAssert.ProblemAsync(await staff.PostAsync("/api/v1/inventory/relocations", Body(5m, true)), HttpStatusCode.Conflict, "inventory.insufficient_available");
        foreach (var role in new[] { Roles.Sales, Roles.Viewer })
        {
            var other = await kit.Owner.AddMemberAsync(fixture, role);
            await HttpAssert.ProblemAsync(await other.PostAsync("/api/v1/inventory/relocations", Body(1m, null)), HttpStatusCode.Forbidden, "auth.forbidden");
        }

        var moved = await Fulfillment.ReadAsync<TransferResult>(await staff.PostAsync("/api/v1/inventory/relocations", Body(4m, true)), HttpStatusCode.Created);
        Assert.Equal(2m, moved.ReservationMoves!.Sum(m => m.Quantity));
        Assert.Equal((1m, 1m), await kit.BalanceAsync(chain, shelves.WarehouseId, shelves.A.Id));
        Assert.Equal((4m, 2m), await kit.BalanceAsync(chain, shelves.WarehouseId, shelves.B.Id));
        var after = await Fulfillment.TaskAsync(staff, task.Task.Id);
        Assert.Equal(
            [("A-01", 1m, 1m), ("B-01", 2m, 0m)],
            after.Lines.Select(l => (l.LocationCode!, l.QuantityToPick, l.QuantityPicked)));
        await kit.AssertConsistentAsync();
        Assert.Equal(0L, await kit.CountAsync(
            """
            SELECT count(*) FROM (
                SELECT transfer_group_id FROM inventory.inventory_movements
                WHERE tenant_id = $1 AND transfer_group_id IS NOT NULL
                GROUP BY transfer_group_id HAVING sum(quantity) <> 0 OR count(*) <> 2) g
            """));

        // The picker continues on the new shelf.
        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, task.Task.Id, "ZINCIR-01", 2m, "B-01"));
        var completed = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.CompleteAsync(staff, task.Task.Id));
        await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(staff, completed.ShipmentId!.Value));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(chain, shelves.WarehouseId, shelves.A.Id));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(chain, shelves.WarehouseId, shelves.B.Id));
        await kit.AssertConsistentAsync();
    }

    // ---- WHT-03: priority and customer name ----

    [Fact]
    public async Task Tasks_carry_priority_and_customer_name_for_staff_and_the_queue_sorts_by_priority()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var first = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var second = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var firstTask = await Fulfillment.ActiveTaskAsync(kit.Owner, first.Order.Id);
        var secondTask = await Fulfillment.ActiveTaskAsync(kit.Owner, second.Order.Id);
        Assert.Equal(("normal", "Sentetik Motor Ltd."), (firstTask.Task.Priority, firstTask.Task.CustomerName));

        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var pool = await Fulfillment.ReadAsync<MyPickingTasksResponse>(await staff.GetAsync("/api/v1/picking-tasks/mine?scope=pool"));
        Assert.Equal([firstTask.Task.Id, secondTask.Task.Id], pool.Items.Select(t => t.Id));
        Assert.All(pool.Items, t => Assert.Equal("Sentetik Motor Ltd.", t.CustomerName));
        Assert.Equal("Sentetik Motor Ltd.", (await Fulfillment.TaskAsync(staff, firstTask.Task.Id)).Task.CustomerName);

        // Only supervisors change the priority; invalid values are refused.
        var path = $"/api/v1/picking-tasks/{secondTask.Task.Id}/priority";
        await HttpAssert.ProblemAsync(await Fulfillment.PostAsync(staff, path, new { priority = "urgent" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await Fulfillment.PostAsync(kit.Owner, path, new { priority = "asap" }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await Fulfillment.PostAsync(kit.Owner, path, new { priority = "urgent" }, ifMatch: 999), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        var urgent = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.PostAsync(kit.Owner, path, new { priority = "urgent" }));
        Assert.Equal("urgent", urgent.Task.Priority);
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.PickingTaskPriorityChanged}'"));

        pool = await Fulfillment.ReadAsync<MyPickingTasksResponse>(await staff.GetAsync("/api/v1/picking-tasks/mine?scope=pool"));
        Assert.Equal([secondTask.Task.Id, firstTask.Task.Id], pool.Items.Select(t => t.Id));
        var filtered = await Fulfillment.ReadAsync<PickingTaskPageResponse>(await kit.Owner.GetAsync("/api/v1/picking-tasks?priority=urgent"));
        Assert.Equal(secondTask.Task.Id, Assert.Single(filtered.Items).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await kit.Owner.GetAsync("/api/v1/picking-tasks?priority=asap")).StatusCode);

        // A finished task's priority is frozen.
        await kit.ShipAsync(first, (first.Lines[0].Id, 1m));
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/picking-tasks/{firstTask.Task.Id}/priority", new { priority = "high" }),
            HttpStatusCode.Conflict,
            "picking.invalid_transition");
    }

    // ---- WHT-01: auto_create_picking_task ----

    [Fact]
    public async Task With_auto_create_off_approval_creates_no_task_and_a_supervisor_creates_it_with_a_priority()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var settings = await kit.Owner.OkAsync<System.Text.Json.JsonElement>(await kit.Owner.GetAsync("/api/v1/tenant/settings"));
        Assert.True(settings.GetProperty("autoCreatePickingTask").GetBoolean());
        await SetSettingsAsync(kit.Owner, new { autoCreatePickingTask = false });
        Assert.Equal(1L, await kit.CountAsync(
            $"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.TenantSettingsChanged}' AND changes::text LIKE '%autoCreatePickingTask%'"));

        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
        Assert.Equal("approved", approved.Order.Status);
        Assert.Equal(1L, await kit.ActiveReservationsAsync());
        var none = await Fulfillment.ReadAsync<PickingTaskPageResponse>(await kit.Owner.GetAsync($"/api/v1/picking-tasks?orderId={approved.Order.Id}"));
        Assert.Empty(none.Items);

        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = approved.Order.Id, priority = "asap" }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
        var created = await Fulfillment.ReadAsync<PickingTaskResponse>(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = approved.Order.Id, priority = "high" }), HttpStatusCode.Created);
        Assert.Equal(("high", 2m, "Sentetik Motor Ltd."), (created.Task.Priority, created.Task.QuantityToPick, created.Task.CustomerName));

        // Turning it back on restores approval-time creation.
        await SetSettingsAsync(kit.Owner, new { autoCreatePickingTask = true });
        var next = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        Assert.Equal("normal", (await Fulfillment.ActiveTaskAsync(kit.Owner, next.Order.Id)).Task.Priority);
        await kit.AssertConsistentAsync();
    }

    // ---- Tenant isolation of the new commands ----

    [Fact]
    public async Task Another_tenants_task_cannot_be_reprioritised_or_reallocated()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 2m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var intruder = await CatalogClient.NewTenantOwnerAsync(fixture);

        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(intruder, $"/api/v1/picking-tasks/{task.Task.Id}/priority", new { priority = "urgent" }), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(intruder, $"/api/v1/picking-tasks/{task.Task.Id}/reallocate"), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Equal("normal", (await Fulfillment.TaskAsync(kit.Owner, task.Task.Id)).Task.Priority);
    }

    // ---- Helpers ----

    internal sealed record Shelves(Guid WarehouseId, LocationResponse A, LocationResponse B, LocationResponse C, LocationResponse D);

    /// <summary>A located warehouse with shelves A-01 … D-01 in walking order.</summary>
    internal static async Task<(OrdersKit Kit, Shelves Shelves)> LocatedAsync(ApiFixture fixture)
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var warehouse = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "RAF", name = "Raflı depo", usesLocations = true });
        async Task<LocationResponse> Shelf(string code, int sequence) =>
            await kit.Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouse.Id}/locations", new { code, barcode = "LOC-" + code, pickSequence = sequence });
        return (kit, new Shelves(warehouse.Id, await Shelf("A-01", 1), await Shelf("B-01", 2), await Shelf("C-01", 3), await Shelf("D-01", 4)));
    }

    internal static async Task<OrderDetailResponse> ApproveAsync(OrdersKit kit, Guid warehouseId, params object[] lines)
    {
        var draft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, lines, warehouseId: warehouseId), HttpStatusCode.Created);
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        return await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"));
    }

    private Task<(OrdersKit Kit, Shelves Shelves)> LocatedAsync() => LocatedAsync(fixture);

    /// <summary>Deactivates a shelf that was not modified since it was created (If-Match = its creation version).</summary>
    internal static async Task DeactivateAsync(OrdersKit kit, LocationResponse location)
    {
        var response = await kit.Owner.PatchAsync($"/api/v1/locations/{location.Id}", new { isActive = false }, CatalogClient.ETag(location.Version));
        Assert.True(response.IsSuccessStatusCode, $"deactivate: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    private static async Task SetSettingsAsync(CatalogClient owner, object patch)
    {
        var current = await owner.GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var response = await owner.PatchAsync("/api/v1/tenant/settings", patch, current.Headers.ETag!.ToString());
        Assert.True(response.IsSuccessStatusCode, $"settings: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }
}
