using System.Data.Common;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Orders.Domain;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BayiOS.Modules.Orders.Application;

/// <summary>
/// Order commands (inventory-and-orders §3–§4). Every command runs in the request UnitOfWork: the order header is
/// locked first (<c>FOR UPDATE</c>, lock hierarchy step 1), the transition is checked by <see cref="OrderStateMachine"/>,
/// stock changes go through <see cref="IInventoryService"/> (balances then reservations, steps 3–4), and status
/// history, audit and outbox rows are written in the same transaction. Any error rolls everything back.
/// Picking, packing and shipping are driven by the Warehouse module through <see cref="IOrderFulfillment"/>; this
/// class is its only implementation and the only caller of <see cref="IInventoryService.IssueAsync"/>.
/// </summary>
internal sealed class OrderService(
    OrdersDbContext db,
    OrderQueries queries,
    IRequestContext requestContext,
    IPriceResolver prices,
    ICustomerPricingDirectory customers,
    ITenantCurrencyProvider tenantCurrency,
    IOrderVariantDirectory variants,
    IWarehouseDirectory warehouses,
    IInventoryService inventory,
    IOrderFulfillmentListener fulfillment,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock) : IOrderFulfillment
{
    public const int MaxLines = 500;
    public const string NumberPrefix = "SIP";

    /// <summary>numeric(18,3): 15 integer digits.</summary>
    public const decimal MaxQuantity = 999_999_999_999_999m;

    private const string ActiveCustomer = "active";

    private static readonly TimeZoneInfo Istanbul = FindIstanbul();

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No user resolved.");

    // ---- Draft create / edit ----

    public async Task<AppResult<OrderDetailResponse>> CreateAsync(CreateOrderRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var source = OrderText.Clean(request.Source) ?? OrderSources.Manual;
        if (!OrderSources.All.Contains(source))
        {
            errors["source"] = [$"Must be one of: {string.Join(", ", OrderSources.All)}."];
        }

        var currency = OrderText.Clean(request.Currency)?.ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)))
        {
            errors["currency"] = ["Must be a 3-letter ISO 4217 code."];
        }

        ValidateHeader(errors, request.Notes);
        if (request.CustomerId is null)
        {
            errors["customerId"] = ["Required."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var customer = await customers.FindAsync(request.CustomerId!.Value, cancellationToken);
        if (CheckCustomer(customer) is { } customerError)
        {
            return customerError;
        }

        var warehouse = request.WarehouseId is { } warehouseId
            ? await warehouses.FindWarehouseAsync(warehouseId, cancellationToken)
            : await warehouses.GetOrCreateDefaultAsync(cancellationToken);
        if (CheckWarehouse(warehouse) is { } warehouseError)
        {
            return warehouseError;
        }

        currency ??= customer!.Currency ?? await tenantCurrency.GetDefaultCurrencyAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var today = Today(now);
        await db.JoinTransactionAsync(cancellationToken);
        var number = await NextNumberAsync(today.Year, cancellationToken);
        var order = Order.Create(
            TenantId, number, customer!.CustomerId, warehouse!.Id, source, request.SourceReferenceId, currency, today, UserId, now);
        order.Notes = OrderText.Clean(request.Notes);
        order.RequestedDeliveryDate = request.RequestedDeliveryDate;
        db.Orders.Add(order);

        if (await ApplyLinesAsync(order, request.Lines ?? [], request.PricesIncludeTax, isNew: true, now, cancellationToken) is { } lineError)
        {
            return lineError;
        }

        db.History.Add(OrderStatusChange.Create(order, OrderActions.Create, null, null, UserId, now, correlationId));
        await audit.RecordAsync(
            new AuditEvent(OrderAuditActions.Created, OrderAuditActions.EntityType, order.Id, new AuditChanges()
                .Value("number", order.Number)
                .Value("source", order.Source)
                .Value("lineCount", order.Lines.Count)
                .Value("grandTotal", order.GrandTotal)
                .ToDictionary()),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(order.Id, cancellationToken) is { } detail ? detail : AppError.NotFound();
    }

    public async Task<AppResult<OrderDetailResponse>> UpdateAsync(
        Guid id, Patch<UpdateOrderRequest> patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var values = patch.Values;
        var errors = new Dictionary<string, string[]>();
        ValidateHeader(errors, values.Notes);
        if (patch.Has("customerId") && values.CustomerId is null)
        {
            errors["customerId"] = ["Cannot be cleared."];
        }

        if (patch.Has("warehouseId") && values.WarehouseId is null)
        {
            errors["warehouseId"] = ["Cannot be cleared."];
        }

        if (patch.Has("lines") && values.Lines is null)
        {
            errors["lines"] = ["Must be an array (use [] to remove every line)."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await WithLockedOrderAsync(id, expectedVersion, async order =>
        {
            if (!OrderStateMachine.IsEditable(order.Status))
            {
                return AppError.NotEditable(order.Status);
            }

            var reprice = false;
            if (patch.Has("customerId") && values.CustomerId != order.CustomerId)
            {
                var customer = await customers.FindAsync(values.CustomerId!.Value, cancellationToken);
                if (CheckCustomer(customer) is { } customerError)
                {
                    return customerError;
                }

                order.CustomerId = customer!.CustomerId;
                reprice = true;
            }

            if (patch.Has("warehouseId") && values.WarehouseId != order.WarehouseId)
            {
                var warehouse = await warehouses.FindWarehouseAsync(values.WarehouseId!.Value, cancellationToken);
                if (CheckWarehouse(warehouse) is { } warehouseError)
                {
                    return warehouseError;
                }

                order.WarehouseId = warehouse!.Id;
            }

            if (patch.Has("notes"))
            {
                order.Notes = OrderText.Clean(values.Notes);
            }

            if (patch.Has("requestedDeliveryDate"))
            {
                order.RequestedDeliveryDate = values.RequestedDeliveryDate;
            }

            bool? taxMode = patch.Has("pricesIncludeTax") ? values.PricesIncludeTax ?? false : null;
            var now = clock.GetUtcNow();
            if (patch.Has("lines") || reprice || (taxMode is { } mode && mode != order.PricesIncludeTax))
            {
                // Draft prices are always "as of the last edit": every edit resolves list prices again for today.
                order.PriceDate = Today(now);
                var inputs = patch.Has("lines")
                    ? values.Lines!
                    : [.. order.Lines.OrderBy(l => l.LineNo).Select(AsInput)];
                if (await ApplyLinesAsync(order, inputs, taxMode, isNew: false, now, cancellationToken) is { } lineError)
                {
                    return lineError;
                }
            }

            order.Touch(UserId, now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }, cancellationToken);
    }

    // ---- Transitions (§3.1) ----

    public Task<AppResult<OrderDetailResponse>> SubmitAsync(Guid id, long? expectedVersion, string? correlationId, CancellationToken cancellationToken) =>
        TransitionAsync(id, expectedVersion, OrderActions.Submit, null, correlationId, async order =>
        {
            if (order.Lines.Count == 0)
            {
                return AppError.Validation("lines", "An order needs at least one line.");
            }

            return CheckPriced(order) ?? CheckCustomer(await customers.FindAsync(order.CustomerId, cancellationToken));
        }, cancellationToken);

    public Task<AppResult<OrderDetailResponse>> WithdrawAsync(Guid id, long? expectedVersion, string? correlationId, CancellationToken cancellationToken) =>
        TransitionAsync(id, expectedVersion, OrderActions.Withdraw, null, correlationId, _ => Task.FromResult<AppError?>(null), cancellationToken);

    public Task<AppResult<OrderDetailResponse>> RejectAsync(
        Guid id, long? expectedVersion, string? reason, string? correlationId, CancellationToken cancellationToken) =>
        WithReason(reason) is { } error
            ? Task.FromResult<AppResult<OrderDetailResponse>>(error)
            : TransitionAsync(id, expectedVersion, OrderActions.Reject, OrderText.Clean(reason), correlationId, _ => Task.FromResult<AppError?>(null), cancellationToken);

    /// <summary>
    /// §4.3: price check (no unpriced line), then every line is reserved in one Inventory call — all or nothing. A
    /// shortage returns 409 <c>inventory.insufficient_stock</c> with the shortages and nothing is reserved.
    /// </summary>
    public Task<AppResult<OrderDetailResponse>> ApproveAsync(
        Guid id, long expectedVersion, bool confirmNegativeStock, string? correlationId, CancellationToken cancellationToken) =>
        TransitionAsync(id, expectedVersion, OrderActions.Approve, null, correlationId, async order =>
        {
            if ((CheckPriced(order) ?? CheckCustomer(await customers.FindAsync(order.CustomerId, cancellationToken))) is { } error)
            {
                return error;
            }

            var lines = order.Lines.OrderBy(l => l.LineNo).ToList();
            var reserved = await inventory.ReserveAsync(
                new ReserveStock(
                    order.Id,
                    order.WarehouseId,
                    [.. lines.Select(l => new ReserveLine(l.Id, l.VariantId, l.Quantity))],
                    confirmNegativeStock,
                    correlationId),
                cancellationToken);
            if (reserved.Error is { } stockError)
            {
                return AppError.FromInventory(stockError);
            }

            foreach (var allocation in reserved.Value!.Reservations)
            {
                lines.Single(l => l.Id == allocation.OrderLineId).QtyReserved += allocation.Quantity;
            }

            var now = clock.GetUtcNow();
            order.ApprovedAt = now;
            order.ApprovedBy = UserId;
            await audit.RecordAsync(
                new AuditEvent(OrderAuditActions.Approved, OrderAuditActions.EntityType, order.Id, new AuditChanges()
                    .Field("status", OrderStatuses.PendingApproval, OrderStatuses.Approved)
                    .Value("grandTotal", order.GrandTotal)
                    .Value("reservations", reserved.Value.Reservations.Count)
                    .ToDictionary()),
                cancellationToken);
            await outbox.PublishAsync(
                OrderEvents.Approved,
                new { orderId = order.Id, number = order.Number, customerId = order.CustomerId, warehouseId = order.WarehouseId },
                cancellationToken);

            // OrderApproved → Warehouse picking task from the reservations just made (same transaction, WHT-01).
            await fulfillment.OnApprovedAsync(
                new OrderApprovedNotice(order.Id, order.Number, order.WarehouseId, [.. lines.Select(ToFulfillment)]), cancellationToken);
            return null;
        }, cancellationToken);

    public Task<AppResult<OrderDetailResponse>> CompleteAsync(Guid id, long? expectedVersion, string? correlationId, CancellationToken cancellationToken) =>
        TransitionAsync(id, expectedVersion, OrderActions.Complete, null, correlationId, order =>
        {
            order.CompletedAt = clock.GetUtcNow();
            return Task.FromResult<AppError?>(null);
        }, cancellationToken);

    /// <summary>§4.4: cancels; an order holding reservations releases exactly their open quantities.</summary>
    public Task<AppResult<OrderDetailResponse>> CancelAsync(
        Guid id, long? expectedVersion, string? reason, string? correlationId, CancellationToken cancellationToken) =>
        WithReason(reason) is { } error
            ? Task.FromResult<AppResult<OrderDetailResponse>>(error)
            : TransitionAsync(id, expectedVersion, OrderActions.Cancel, OrderText.Clean(reason), correlationId, async order =>
            {
                var from = order.Status;
                decimal released = 0;
                if (OrderStateMachine.HoldsReservations(from))
                {
                    // Warehouse documents (lock hierarchy step 2) before balances/reservations (steps 3–4): open
                    // picking tasks and unconfirmed shipments are cancelled, picked goods get a putaway task (ORD-07).
                    await fulfillment.OnReservationsReleasedAsync(order.Id, FulfillmentReleaseReasons.Cancelled, cancellationToken);
                    var release = await ReleaseAsync(order, correlationId, cancellationToken);
                    if (release.Error is { } releaseError)
                    {
                        return releaseError;
                    }

                    released = release.Value;
                }

                var now = clock.GetUtcNow();
                order.CancelledAt = now;
                order.CancelledBy = UserId;
                order.CancelReason = OrderText.Clean(reason);
                await audit.RecordAsync(
                    new AuditEvent(OrderAuditActions.Cancelled, OrderAuditActions.EntityType, order.Id, new AuditChanges()
                        .Field("status", from, OrderStatuses.Cancelled)
                        .Value("reason", order.CancelReason)
                        .Value("releasedQuantity", released)
                        .ToDictionary()),
                    cancellationToken);
                await outbox.PublishAsync(
                    OrderEvents.Cancelled, new { orderId = order.Id, number = order.Number, fromStatus = from }, cancellationToken);
                return null;
            }, cancellationToken);

    /// <summary>§3.1 6s: after a partial shipment, closes the rest (qty_cancelled) and releases its reservations.</summary>
    public Task<AppResult<OrderDetailResponse>> CloseRemainingAsync(
        Guid id, long? expectedVersion, string? reason, string? correlationId, CancellationToken cancellationToken) =>
        WithReason(reason) is { } error
            ? Task.FromResult<AppResult<OrderDetailResponse>>(error)
            : TransitionAsync(id, expectedVersion, OrderActions.CloseRemaining, OrderText.Clean(reason), correlationId, async order =>
            {
                // Lock hierarchy: Warehouse documents (step 2) before the release touches balances (step 3).
                await fulfillment.OnReservationsReleasedAsync(order.Id, FulfillmentReleaseReasons.Closed, cancellationToken);
                var release = await ReleaseAsync(order, correlationId, cancellationToken);
                if (release.Error is { } releaseError)
                {
                    return releaseError;
                }

                var closed = 0m;
                foreach (var line in order.Lines.Where(l => l.Remaining > 0))
                {
                    closed += line.Remaining;
                    line.QtyCancelled += line.Remaining;
                }

                order.ShippedAt = clock.GetUtcNow();
                await audit.RecordAsync(
                    new AuditEvent(OrderAuditActions.RemainingClosed, OrderAuditActions.EntityType, order.Id, new AuditChanges()
                        .Value("reason", OrderText.Clean(reason))
                        .Value("closedQuantity", closed)
                        .Value("releasedQuantity", release.Value)
                        .ToDictionary()),
                    cancellationToken);
                await outbox.PublishAsync(
                    OrderEvents.Shipped,
                    new { orderId = order.Id, number = order.Number, shipmentId = (Guid?)null, status = OrderStatuses.Shipped, fulfillmentStatus = order.FulfillmentStatus },
                    cancellationToken);
                return null;
            }, cancellationToken);

    // ---- IOrderFulfillment (called by Warehouse inside its request transaction) ----

    public async Task<FulfillmentResult<FulfillmentOrder>> LockAsync(Guid orderId, CancellationToken cancellationToken) =>
        ToFulfillment(await InLockedOrderAsync(orderId, null, order => Task.FromResult<AppResult<FulfillmentOrder>>(Snapshot(order)), cancellationToken));

    public async Task<FulfillmentResult<FulfillmentOrder>> StartPickingAsync(Guid orderId, string? correlationId, CancellationToken cancellationToken) =>
        ToFulfillment(await InLockedOrderAsync<FulfillmentOrder>(orderId, null, async order =>
        {
            if (order.Status is OrderStatuses.Picking or OrderStatuses.Packed)
            {
                return Snapshot(order);
            }

            return await ApplyTransitionAsync(order, OrderActions.StartPicking, null, correlationId, NoEffect, false, cancellationToken) is { } error
                ? error
                : Snapshot(order);
        }, cancellationToken));

    public async Task<FulfillmentResult<FulfillmentOrder>> MarkPackedAsync(Guid orderId, string? correlationId, CancellationToken cancellationToken) =>
        ToFulfillment(await InLockedOrderAsync<FulfillmentOrder>(orderId, null, async order =>
        {
            if (order.Status == OrderStatuses.Packed)
            {
                return Snapshot(order);
            }

            return await ApplyTransitionAsync(order, OrderActions.Pack, null, correlationId, NoEffect, false, cancellationToken) is { } error
                ? error
                : Snapshot(order);
        }, cancellationToken));

    public async Task<FulfillmentResult<FulfillmentOrder>> ReportShortageAsync(
        Guid orderId, string reason, string? correlationId, CancellationToken cancellationToken) =>
        ToFulfillment(await InLockedOrderAsync<FulfillmentOrder>(orderId, null, async order =>
        {
            var error = await ApplyTransitionAsync(order, OrderActions.ReportShortage, reason, correlationId, o =>
            {
                o.HasShortage = true;
                return Task.FromResult<AppError?>(null);
            }, false, cancellationToken);
            return error is null ? Snapshot(order) : error;
        }, cancellationToken));

    /// <summary>
    /// §4.6: the quantities come from the Warehouse shipment (each line names its order line and reserved location).
    /// Lines may not exceed what is open on the order line (I7); the stock is issued from the reservations of that
    /// line + location (<c>shipment_out</c>; available stock unchanged). Partial shipments keep the status and set
    /// fulfillment <c>partial</c>; the shipment that leaves nothing open moves the order to shipped.
    /// </summary>
    public async Task<FulfillmentResult<OrderShipmentRecorded>> ShipAsync(ShipOrderCommand command, CancellationToken cancellationToken)
    {
        var inputs = command.Lines;
        if (inputs.Count is 0 or > MaxLines
            || inputs.Any(l => !IsQuantity(l.Quantity))
            || inputs.Select(l => l.ShipmentLineId).Distinct().Count() != inputs.Count)
        {
            return new FulfillmentError(422, "validation.failed", "Shipment lines are invalid", new Dictionary<string, string[]>
            {
                ["lines"] = [$"Between 1 and {MaxLines} distinct lines with a quantity greater than 0 (at most 3 decimals) are required."],
            });
        }

        return ToFulfillment(await InLockedOrderAsync<OrderShipmentRecorded>(command.OrderId, null, async order =>
        {
            IReadOnlyList<Guid> movementIds = [];
            var error = await ApplyTransitionAsync(order, OrderActions.Ship, null, command.CorrelationId, async o =>
            {
                var lines = o.Lines.ToDictionary(l => l.Id);
                var errors = new Dictionary<string, string[]>();
                var shipping = new Dictionary<Guid, decimal>();
                for (var i = 0; i < inputs.Count; i++)
                {
                    if (!lines.TryGetValue(inputs[i].OrderLineId, out var line))
                    {
                        errors[$"lines[{i}].orderLineId"] = ["Line not found in this order."];
                        continue;
                    }

                    shipping[line.Id] = shipping.GetValueOrDefault(line.Id) + inputs[i].Quantity;
                }

                foreach (var (lineId, quantity) in shipping.Where(s => s.Value > lines[s.Key].Remaining))
                {
                    errors[$"lines[{inputs.ToList().FindIndex(l => l.OrderLineId == lineId)}].quantity"] =
                        [$"Exceeds the open quantity of the line ({lines[lineId].Remaining})."];
                }

                if (errors.Count > 0)
                {
                    return new AppError(422, "order.ship_exceeds_remaining", "Shipment lines are invalid for this order", errors);
                }

                var shipment = OrderShipment.Create(
                    command.ShipmentId,
                    o,
                    OrderText.Clean(command.Carrier),
                    OrderText.Clean(command.TrackingNumber),
                    command.PackageCount,
                    UserId,
                    clock.GetUtcNow(),
                    command.CorrelationId);
                foreach (var input in inputs)
                {
                    shipment.Lines.Add(OrderShipmentLine.Create(input.ShipmentLineId, shipment, lines[input.OrderLineId], input.LocationId, input.Quantity));
                }

                var issued = await inventory.IssueAsync(
                    new IssueStock(
                        shipment.Id,
                        o.WarehouseId,
                        [.. shipment.Lines.Select(l => new IssueLine(l.Id, l.OrderLineId, l.VariantId, l.LocationId, l.Quantity))],
                        command.CorrelationId),
                    cancellationToken);
                if (issued.Error is { } issueError)
                {
                    return AppError.FromInventory(issueError);
                }

                movementIds = [.. issued.Value!.Movements.Select(m => m.Id)];
                foreach (var shipped in shipment.Lines)
                {
                    var line = lines[shipped.OrderLineId];
                    line.QtyShipped += shipped.Quantity;
                    line.QtyReserved -= shipped.Quantity;
                }

                db.Shipments.Add(shipment);
                var everything = o.Lines.All(l => l.Remaining == 0);
                o.FulfillmentStatus = everything ? FulfillmentStatuses.Full : FulfillmentStatuses.Partial;
                o.Status = OrderStateMachine.Next(OrderActions.Ship, o.Status, o.FulfillmentStatus, everything)!;
                if (everything)
                {
                    o.ShippedAt = shipment.ShippedAt;
                }

                await audit.RecordAsync(
                    new AuditEvent(OrderAuditActions.Shipped, OrderAuditActions.EntityType, o.Id, new AuditChanges()
                        .Value("shipmentId", shipment.Id)
                        .Value("quantity", shipment.Lines.Sum(l => l.Quantity))
                        .Value("fulfillmentStatus", o.FulfillmentStatus)
                        .ToDictionary()),
                    cancellationToken);
                await outbox.PublishAsync(
                    OrderEvents.Shipped,
                    new { orderId = o.Id, number = o.Number, shipmentId = (Guid?)shipment.Id, status = o.Status, fulfillmentStatus = o.FulfillmentStatus },
                    cancellationToken);
                return null;
            }, null, cancellationToken);
            return error is null
                ? new OrderShipmentRecorded(order.Id, order.Status, order.FulfillmentStatus, order.RowVersion, movementIds)
                : error;
        }, cancellationToken));
    }

    // ---- Shared ----

    private static readonly Func<Order, Task<AppError?>> NoEffect = _ => Task.FromResult<AppError?>(null);

    private Task<AppResult<OrderDetailResponse>> TransitionAsync(
        Guid id,
        long? expectedVersion,
        string action,
        string? reason,
        string? correlationId,
        Func<Order, Task<AppError?>> effect,
        CancellationToken cancellationToken) =>
        WithLockedOrderAsync(
            id, expectedVersion, order => ApplyTransitionAsync(order, action, reason, correlationId, effect, false, cancellationToken), cancellationToken);

    /// <summary>
    /// Checks <paramref name="action"/> against <see cref="OrderStateMachine"/>, runs the effect (which sees the order
    /// still in its source status), sets the target, writes the history row and saves. The order must be locked.
    /// </summary>
    /// <param name="shipsEverything">For <c>ship</c> the effect decides the target itself (null); other actions get it up front.</param>
    private async Task<AppError?> ApplyTransitionAsync(
        Order order,
        string action,
        string? reason,
        string? correlationId,
        Func<Order, Task<AppError?>> effect,
        bool? shipsEverything,
        CancellationToken cancellationToken)
    {
        var from = order.Status;
        // For ship the final status depends on the quantities: any legal "ship" source is accepted here.
        if (OrderStateMachine.Next(action, from, order.FulfillmentStatus, shipsEverything ?? false) is not { } target)
        {
            return AppError.InvalidTransition(action, from);
        }

        if (await effect(order) is { } error)
        {
            return error;
        }

        var now = clock.GetUtcNow();
        if (shipsEverything is not null)
        {
            order.Status = target;
            if (action == OrderActions.Submit)
            {
                order.SubmittedAt = now;
                order.SubmittedBy = UserId;
            }
        }

        db.History.Add(OrderStatusChange.Create(order, action, from, reason, UserId, now, correlationId));
        order.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>Locks the header (lock hierarchy step 1), checks the version, runs the change and returns the detail.</summary>
    private async Task<AppResult<OrderDetailResponse>> WithLockedOrderAsync(
        Guid id, long? expectedVersion, Func<Order, Task<AppError?>> change, CancellationToken cancellationToken)
    {
        var changed = await InLockedOrderAsync<bool>(
            id, expectedVersion, async order => await change(order) is { } error ? error : true, cancellationToken);
        if (changed.Error is { } changeError)
        {
            return changeError;
        }

        return await queries.DetailAsync(id, cancellationToken) is { } detail ? detail : AppError.NotFound();
    }

    /// <summary>Joins the request transaction, locks the header (<c>FOR UPDATE</c>, 5 s), loads it with its lines.</summary>
    private async Task<AppResult<T>> InLockedOrderAsync<T>(
        Guid id, long? expectedVersion, Func<Order, Task<AppResult<T>>> change, CancellationToken cancellationToken)
    {
        try
        {
            await db.JoinTransactionAsync(cancellationToken);
            if (!await LockHeaderAsync(id, cancellationToken))
            {
                return AppError.NotFound();
            }

            var order = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == id, cancellationToken);
            if (expectedVersion is { } version && version != order.RowVersion)
            {
                return AppError.VersionMismatch();
            }

            return await change(order);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            return AppError.Busy();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected })
        {
            return AppError.Busy();
        }
    }

    private static FulfillmentOrder Snapshot(Order order) => new(
        order.Id,
        order.Number,
        order.WarehouseId,
        order.Status,
        order.FulfillmentStatus,
        OrderStateMachine.HoldsReservations(order.Status),
        order.RowVersion,
        [.. order.Lines.OrderBy(l => l.LineNo).Select(ToFulfillment)]);

    private static FulfillmentOrderLine ToFulfillment(OrderLine line) => new(
        line.Id, line.LineNo, line.VariantId, line.Sku, line.Name, line.Unit, line.Quantity, line.QtyReserved, line.QtyShipped, line.QtyCancelled);

    private static FulfillmentResult<T> ToFulfillment<T>(AppResult<T> result) =>
        result.Error is { } error ? new FulfillmentError(error.Status, error.Code, error.Title, error.Errors) : result.Value!;

    /// <summary>Releases every open reservation of the order; returns the released total and updates the line mirrors.</summary>
    private async Task<AppResult<decimal>> ReleaseAsync(Order order, string? correlationId, CancellationToken cancellationToken)
    {
        var release = await inventory.ReleaseAsync(new ReleaseReservations(order.Id, null, correlationId), cancellationToken);
        if (release.Error is { } error)
        {
            return AppError.FromInventory(error);
        }

        var lines = order.Lines.ToDictionary(l => l.Id);
        foreach (var released in release.Value!.Reservations)
        {
            lines[released.OrderLineId].QtyReserved -= released.Quantity;
        }

        return release.Value.Reservations.Sum(r => r.Quantity);
    }

    /// <summary>
    /// Replaces the order's lines with <paramref name="inputs"/>: validates them, resolves list prices through
    /// <see cref="IPriceResolver"/> (no_price stays null, never 0), applies overrides (permission-checked) and
    /// discounts, renumbers lines and recomputes the totals.
    /// </summary>
    private async Task<AppError?> ApplyLinesAsync(
        Order order, IReadOnlyList<OrderLineInput> inputs, bool? taxMode, bool isNew, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var existing = order.Lines.ToDictionary(l => l.Id);
        if (inputs.Count > MaxLines)
        {
            errors["lines"] = [$"At most {MaxLines} lines."];
            return AppError.Validation(errors);
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var field = $"lines[{i}]";
            if (inputs[i] is not { } input)
            {
                errors[field] = ["Required."];
                continue;
            }

            if (input.VariantId is null)
            {
                errors[field + ".variantId"] = ["Required."];
            }

            if (input.Quantity is not { } quantity || !IsQuantity(quantity))
            {
                errors[field + ".quantity"] = ["Must be greater than 0 with at most 3 decimals."];
            }

            if (input.UnitPrice is { } price && (price < 0 || price >= OrderMath.MaxAmount || OrderMath.Round4(price) != price))
            {
                errors[field + ".unitPrice"] = ["Must be 0 or more with at most 4 decimals."];
            }

            if (input.DiscountPercent is { } percent && (percent is < 0 or > 100 || OrderMath.Round2(percent) != percent))
            {
                errors[field + ".discountPercent"] = ["Must be between 0 and 100 with at most 2 decimals."];
            }

            if (input.DiscountAmount is { } amount && (amount < 0 || amount >= OrderMath.MaxAmount || OrderMath.Round2(amount) != amount))
            {
                errors[field + ".discountAmount"] = ["Must be 0 or more with at most 2 decimals."];
            }

            if (input.DiscountPercent is not null && input.DiscountAmount is not null)
            {
                errors[field + ".discountAmount"] = ["Give either discountPercent or discountAmount, not both."];
            }

            if (input.Id is { } lineId && (!existing.ContainsKey(lineId) || !seen.Add(lineId)))
            {
                errors[field + ".id"] = ["Line not found in this order (or listed twice)."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var found = await variants.FindVariantsAsync([.. inputs.Select(l => l.VariantId!.Value).Distinct()], cancellationToken);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var keptSameVariant = input.Id is { } lineId && existing[lineId].VariantId == input.VariantId;
            if (!found.TryGetValue(input.VariantId!.Value, out var variant))
            {
                errors[$"lines[{i}].variantId"] = ["Variant not found."];
            }
            else if (!variant.IsActive && !keptSameVariant)
            {
                errors[$"lines[{i}].variantId"] = ["Variant is not active."];
            }
            else if (!variant.AllowsFraction && input.Quantity!.Value != decimal.Truncate(input.Quantity.Value))
            {
                errors[$"lines[{i}].quantity"] = ["The variant's unit does not allow fractions."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        PriceQuote? quote = null;
        if (inputs.Count > 0)
        {
            var quoted = await prices.QuoteAsync(
                new PriceQuoteRequest(
                    order.CustomerId, order.PriceDate, [.. inputs.Select(l => new PriceQuoteLine(l.VariantId!.Value, l.Quantity!.Value))], order.Currency),
                cancellationToken);
            if (quoted.Error is { } pricingError)
            {
                return new AppError(pricingError.Status, pricingError.Code, pricingError.Title, pricingError.Errors);
            }

            quote = quoted.Value!;
        }

        order.PricesIncludeTax = taxMode
            ?? (isNew
                ? quote?.Lines.FirstOrDefault(l => l.Status == PriceStatuses.Priced)?.PricesIncludeTax ?? false
                : order.PricesIncludeTax);

        var groupDiscount = quote?.DealerGroupDiscountPercent is > 0 ? quote.DealerGroupDiscountPercent : null;
        var canOverride = RolePermissions.IsGranted(requestContext.Role, Permissions.OrdersPriceOverride);
        var result = new List<OrderLine>(inputs.Count);
        var overrides = new List<(OrderLine Line, decimal? PriorPrice, decimal? PriorPercent, decimal? PriorAmount)>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var resolved = quote!.Lines[i];
            var variant = found[input.VariantId!.Value];
            var prior = input.Id is { } lineId ? existing[lineId] : null;

            var percent = input.DiscountPercent;
            var amount = input.DiscountAmount;
            var explicitDiscount = percent is not null || amount is not null;
            if (!explicitDiscount && groupDiscount is not null)
            {
                percent = groupDiscount;
            }

            var priceOverride = input.UnitPrice is { } overridePrice
                && !(prior is { PriceOverridden: true } && prior.UnitPrice == overridePrice);
            var discountOverride = explicitDiscount
                && (percent ?? amount) != 0m
                && !(amount is null && percent == groupDiscount)
                && !(prior is not null && prior.DiscountPercent == percent && prior.DiscountAmount == amount);
            if ((priceOverride || discountOverride) && !canOverride)
            {
                return AppError.Forbidden(Permissions.OrdersPriceOverride);
            }

            var line = prior ?? OrderLine.Create(order.TenantId, order.Id, UserId, now);
            var priorPrice = prior?.UnitPrice;
            var priorPercent = prior?.DiscountPercent;
            var priorAmount = prior?.DiscountAmount;
            line.VariantId = variant.Id;
            line.Sku = variant.Sku;
            line.Name = variant.Name;
            line.Unit = variant.Unit;
            line.Quantity = input.Quantity!.Value;
            line.TaxRate = resolved.TaxRate;
            line.DiscountPercent = percent;
            line.DiscountAmount = amount;
            if (input.UnitPrice is { } manual)
            {
                line.UnitPrice = manual;
                line.PriceOverridden = true;
                line.PriceSource = LinePriceSources.Manual;
                line.PriceListId = null;
            }
            else if (resolved.Status == PriceStatuses.Priced && resolved.UnitPrice is { } listPrice)
            {
                line.UnitPrice = OrderMath.ConvertUnitPrice(listPrice, resolved.TaxRate, resolved.PricesIncludeTax ?? false, order.PricesIncludeTax);
                line.PriceOverridden = false;
                line.PriceSource = resolved.Source;
                line.PriceListId = resolved.PriceListId;
            }
            else
            {
                line.UnitPrice = null;
                line.PriceOverridden = false;
                line.PriceSource = null;
                line.PriceListId = null;
            }

            if (line.UnitPrice is { } unitPrice)
            {
                if (OrderMath.Line(line.Quantity, unitPrice, percent, amount) is not { } amounts)
                {
                    errors[$"lines[{i}]"] = ["The line amount is too large or the discount exceeds the line subtotal."];
                    continue;
                }

                line.LineSubtotal = amounts.Subtotal;
                line.LineDiscount = amounts.Discount;
                line.LineTotal = amounts.Total;
            }
            else
            {
                line.LineSubtotal = null;
                line.LineDiscount = null;
                line.LineTotal = null;
            }

            if (priceOverride || discountOverride)
            {
                overrides.Add((line, priorPrice, priorPercent, priorAmount));
            }

            line.LineNo = i + 1;
            line.UpdatedAt = prior is null ? null : now;
            line.UpdatedBy = prior is null ? null : UserId;
            result.Add(line);
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        foreach (var removed in existing.Values.Where(l => !result.Contains(l)))
        {
            order.Lines.Remove(removed);
            db.Lines.Remove(removed);
        }

        foreach (var added in result.Where(l => !existing.ContainsKey(l.Id)))
        {
            order.Lines.Add(added);
            db.Lines.Add(added);
        }

        order.Recalculate();
        if (order.GrandTotal >= OrderMath.MaxAmount)
        {
            return AppError.Validation("lines", "The order total is too large.");
        }

        foreach (var (line, priorPrice, priorPercent, priorAmount) in overrides)
        {
            await audit.RecordAsync(
                new AuditEvent(OrderAuditActions.PriceOverridden, OrderAuditActions.LineEntityType, line.Id, new AuditChanges()
                    .Value("orderId", order.Id)
                    .Value("variantId", line.VariantId)
                    .Field("unitPrice", priorPrice, line.UnitPrice)
                    .Field("discountPercent", priorPercent, line.DiscountPercent)
                    .Field("discountAmount", priorAmount, line.DiscountAmount)
                    .ToDictionary()),
                cancellationToken);
        }

        return null;
    }

    private static OrderLineInput AsInput(OrderLine line) =>
        new(line.Id, line.VariantId, line.Quantity, line.PriceOverridden ? line.UnitPrice : null, line.DiscountPercent, line.DiscountAmount);

    private async Task<bool> LockHeaderAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            SET LOCAL lock_timeout = '5s';
            SELECT 1 FROM orders.orders WHERE tenant_id = @tenant AND id = @id FOR UPDATE;
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", TenantId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Gapless per tenant and year: the counter row is locked until the creating transaction ends.</summary>
    private async Task<string> NextNumberAsync(int year, CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            INSERT INTO orders.order_number_sequences (tenant_id, year, next_value) VALUES (@tenant, @year, 1)
            ON CONFLICT (tenant_id, year) DO UPDATE SET next_value = order_number_sequences.next_value + 1
            RETURNING next_value;
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", TenantId));
        command.Parameters.Add(new NpgsqlParameter<int>("year", year));
        var value = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return $"{NumberPrefix}-{year:D4}-{value:D6}";
    }

    private DbCommand Command(string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static AppError? CheckPriced(Order order)
    {
        var unpriced = order.Lines.OrderBy(l => l.LineNo).Where(l => l.UnitPrice is null).ToList();
        return unpriced.Count == 0
            ? null
            : new AppError(
                422,
                "order.price_missing",
                "Some lines have no price; set a price list price or a manual price first",
                unpriced.ToDictionary(l => $"lines[{l.LineNo - 1}].unitPrice", l => new[] { $"No price for {l.Sku}." }));
    }

    private static AppError? CheckCustomer(CustomerPricingProfile? customer) => customer switch
    {
        null => AppError.Validation("customerId", "Customer not found."),
        { Status: not ActiveCustomer } => AppError.Validation("customerId", "Customer is not active."),
        _ => null,
    };

    private static AppError? CheckWarehouse(WarehouseInfo? warehouse) => warehouse switch
    {
        null => AppError.Validation("warehouseId", "Warehouse not found."),
        { IsActive: false } => AppError.Validation("warehouseId", "Warehouse is inactive."),
        _ => null,
    };

    private static AppError? WithReason(string? reason) => OrderText.Clean(reason) switch
    {
        null => AppError.Validation("reason", "Required."),
        { Length: > OrderText.MaxReasonLength } => AppError.Validation("reason", $"At most {OrderText.MaxReasonLength} characters."),
        _ => null,
    };

    private static void ValidateHeader(Dictionary<string, string[]> errors, string? notes)
    {
        if (notes?.Length > OrderText.MaxNotesLength)
        {
            errors["notes"] = [$"At most {OrderText.MaxNotesLength} characters."];
        }
    }

    private static bool IsQuantity(decimal quantity) =>
        quantity > 0 && quantity <= MaxQuantity && decimal.Round(quantity, 3) == quantity;

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Istanbul).DateTime);

    private static TimeZoneInfo FindIstanbul()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Europe/Istanbul", TimeSpan.FromHours(3), "Europe/Istanbul", "Europe/Istanbul");
        }
    }
}

/// <summary>Audit action codes of the order lifecycle (threat-model §7 naming).</summary>
internal static class OrderAuditActions
{
    public const string EntityType = "order";
    public const string LineEntityType = "order_line";

    public const string Created = AuditActions.OrderCreated;
    public const string Approved = AuditActions.OrderApproved;
    public const string Cancelled = AuditActions.OrderCancelled;
    public const string Shipped = AuditActions.OrderShipped;
    public const string RemainingClosed = AuditActions.OrderRemainingClosed;
    public const string PriceOverridden = AuditActions.OrderPriceOverridden;
}

/// <summary>Outbox event types (ids only — consumers read the rest; at-least-once delivery).</summary>
internal static class OrderEvents
{
    public const string Approved = "orders.approved.v1";
    public const string Cancelled = "orders.cancelled.v1";
    public const string Shipped = "orders.shipped.v1";
}
