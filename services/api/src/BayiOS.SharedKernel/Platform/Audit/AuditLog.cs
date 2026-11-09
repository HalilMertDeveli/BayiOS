namespace BayiOS.SharedKernel.Platform.Audit;

/// <summary>
/// Appends audit events (threat-model §7) to <c>audit.audit_events</c> on the <b>current</b> UnitOfWork transaction,
/// so an event commits or rolls back together with the business change it describes. Tenant, actor, role and
/// correlation id come from the request (or job) context — never from the caller.
/// </summary>
public interface IAuditLog
{
    /// <summary>Requires an active unit of work with a resolved tenant.</summary>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

/// <param name="Action">Dotted code from threat-model §7, e.g. <c>tenant.settings_changed</c>.</param>
/// <param name="EntityType">Snake-case entity name, e.g. <c>tenant_settings</c>.</param>
/// <param name="Changes">Minimal summary (see <see cref="AuditChanges"/>); no secrets, PII only as a "changed" marker.</param>
public sealed record AuditEvent(string Action, string EntityType, Guid? EntityId, IReadOnlyDictionary<string, object?>? Changes = null);

/// <summary>Action codes in use (threat-model §7).</summary>
public static class AuditActions
{
    public const string TenantCreated = "tenant.created";
    public const string TenantSettingsChanged = "tenant.settings_changed";

    /// <summary>An adjustable tenant rule of roles-permissions §4 changed (who may approve, discount limit, cost visibility…).</summary>
    public const string TenantRulesChanged = "tenant.rules_changed";

    // Members and invitations (#129). No e-mail address and never a token in the payload (data-policy §8).
    public const string MemberInvited = "member.invited";
    public const string MemberInviteResent = "member.invite_resent";
    public const string MemberInviteRevoked = "member.invite_revoked";
    public const string MemberJoined = "member.joined";
    public const string MemberRoleChanged = "member.role_changed";
    public const string MemberDisabled = "member.disabled";
    public const string MemberEnabled = "member.enabled";
    public const string MemberOwnershipTransferred = "member.ownership_transferred";
    public const string InventoryReceiptPosted = "inventory.receipt_posted";
    public const string InventoryAdjustmentPosted = "inventory.adjustment_posted";

    /// <summary>A minimum stock threshold was created or changed (#137); the payload carries variant, warehouse, from/to.</summary>
    public const string InventoryThresholdSet = "inventory.threshold_set";

    /// <summary>A minimum stock threshold was removed (#137).</summary>
    public const string InventoryThresholdRemoved = "inventory.threshold_removed";

    /// <summary>
    /// A stock change that went below zero after an explicit confirmation under the former <c>warn</c> policy. No longer
    /// recorded (OD-09: stock never goes below zero); kept so existing audit rows stay readable and filterable.
    /// </summary>
    public const string InventoryNegativeStockConfirmed = "inventory.negative_stock_confirmed";

    /// <summary>Transfer legs were posted (dispatch, receipt, return, write-off or a shelf move); payload: counts and quantities.</summary>
    public const string InventoryTransferPosted = "inventory.transfer_posted";

    /// <summary>Reservations moved shelves inside a warehouse (#98); payload: reason, moves (order line, from, to, quantity).</summary>
    public const string InventoryReservationReallocated = "inventory.reservation_reallocated";

    /// <summary>A stock count was applied: count_correction movements for its differences.</summary>
    public const string InventoryCountPosted = "inventory.count_posted";

    // Warehouse transfer documents (OD-01, #168).
    public const string StockTransferCreated = "stock_transfer.created";
    public const string StockTransferDispatched = "stock_transfer.dispatched";
    public const string StockTransferReceived = "stock_transfer.received";

    /// <summary>The in-transit rest was closed as lost (write-off) or returned to the source; payload: resolution, quantity.</summary>
    public const string StockTransferClosed = "stock_transfer.closed";
    public const string StockTransferCancelled = "stock_transfer.cancelled";

    // Stock counts (INV-06, #168).
    public const string StockCountCreated = "stock_count.created";
    public const string StockCountSubmitted = "stock_count.submitted";
    public const string StockCountRecountRequested = "stock_count.recount_requested";
    public const string StockCountApplied = "stock_count.applied";
    public const string StockCountCancelled = "stock_count.cancelled";

    // Order lifecycle (inventory-and-orders §3.1).
    public const string OrderCreated = "order.created";
    public const string OrderApproved = "order.approved";
    public const string OrderCancelled = "order.cancelled";
    public const string OrderShipped = "order.shipped";

    /// <summary>Short-close: the rest of a partially shipped order is closed and its reservations released.</summary>
    public const string OrderRemainingClosed = "order.remaining_closed";

    /// <summary>A line price or discount set outside the resolved list price (orders.price.override).</summary>
    public const string OrderPriceOverridden = "order.price_overridden";

    /// <summary>A pick task finished (every line picked or reported short).</summary>
    public const string OrderPickProgress = "order.pick_progress";

    // Warehouse fulfilment (picking tasks, shipments).
    public const string PickingTaskCreated = "picking.task_created";
    public const string PickingTaskAssigned = "picking.task_assigned";
    public const string PickingTaskCancelled = "picking.task_cancelled";

    /// <summary>Open task lines were re-pointed to other shelves after their reservations moved (#98).</summary>
    public const string PickingTaskReallocated = "picking.task_reallocated";

    /// <summary>A supervisor changed a task's priority (#98, WHT-03).</summary>
    public const string PickingTaskPriorityChanged = "picking.task_priority_changed";

    /// <summary>A picking or packing shortage (WHT-06); the order is flagged.</summary>
    public const string PickingShortageReported = "picking.shortage_reported";
    public const string ShipmentPacked = "shipment.packed";

    /// <summary>Stock left the building: shipment_out movements were written for the shipment.</summary>
    public const string ShipmentConfirmed = "shipment.confirmed";

    /// <summary>An unconfirmed shipment was cancelled; its picked goods go back to the shelf (putaway), no stock moved.</summary>
    public const string ShipmentCancelled = "shipment.cancelled";
}

/// <summary>
/// Builds the <c>changes</c> document: only fields whose value changed, as <c>{"field": {"from": …, "to": …}}</c>.
/// <see cref="Redacted"/> records just <c>{"changed": true}</c> for personal data (data-policy §8: no PII in audit).
/// </summary>
public sealed class AuditChanges
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public bool HasChanges => _values.Count > 0;

    public AuditChanges Field<T>(string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            _values[name] = new Dictionary<string, object?> { ["from"] = before, ["to"] = after };
        }

        return this;
    }

    public AuditChanges Redacted<T>(string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            _values[name] = new Dictionary<string, object?> { ["changed"] = true };
        }

        return this;
    }

    /// <summary>A plain value (not a diff), e.g. the quantity of a posted movement.</summary>
    public AuditChanges Value(string name, object? value)
    {
        _values[name] = value;
        return this;
    }

    public IReadOnlyDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(_values, StringComparer.Ordinal);
}
