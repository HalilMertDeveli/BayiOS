using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>Stable Problem Details codes of picking, packing and shipping (api-strategy §6).</summary>
internal static class FulfillmentErrors
{
    public static AppError NotAssignee() =>
        new(403, "picking.not_assignee", "This task is not assigned to you; claim it from the pool or ask a manager to assign it");

    public static AppError TaskTransition(string action, string status) =>
        new(409, "picking.invalid_transition", $"Action '{action}' is not allowed for a picking task in status '{status}'");

    public static AppError ShipmentTransition(string action, string status) =>
        new(409, "shipment.invalid_transition", $"Action '{action}' is not allowed for a shipment in status '{status}'");

    public static AppError OrderNotFulfillable(string status) =>
        new(409, "picking.order_not_fulfillable", $"The order is '{status}'; only approved, picking or packed orders are picked or shipped");

    public static AppError Busy() => new(409, "picking.busy", "The task or shipment is locked by another operation; retry shortly");

    public static AppError Validation(string field, string message) =>
        AppError.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static AppError FromOrders(FulfillmentError error) => new(error.Status, error.Code, error.Title, error.Errors);
}

/// <summary>
/// Who may see and act on a task (WHT-02, roles-permissions §3.6). Supervisors hold <c>tasks.assign</c> (owner,
/// admin, manager) and act on any task. Everyone else — warehouse_staff — sees tasks assigned to them plus the
/// active unassigned pool, and acts only on tasks assigned to them (claim first). No K/O grant is involved, so this
/// does not wait for #69.
/// </summary>
internal sealed class FulfillmentAccess(IRequestContext requestContext)
{
    public Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    public Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public bool IsSupervisor => RolePermissions.IsGranted(requestContext.Role, Permissions.TasksAssign);

    public bool CanSee(Guid? assignedTo, string status) =>
        IsSupervisor || assignedTo == UserId || (assignedTo is null && PickingTaskStatuses.IsActive(status));

    public bool CanAct(Guid? assignedTo) => IsSupervisor || assignedTo == UserId;

    /// <summary>A shipment is the continuation of its pick task: same visibility/acting rules via the task's assignee.</summary>
    public bool CanActOnShipment(Guid? taskAssignee) => IsSupervisor || taskAssignee == UserId;
}

/// <summary>
/// Row locks on Warehouse documents (lock hierarchy step 2, inventory-and-orders §2.2). Callers lock the order header
/// first (through <see cref="IOrderFulfillment"/>) whenever the operation may change the order or stock.
/// </summary>
internal sealed class FulfillmentLocks(WarehouseDbContext db, UnitOfWork unitOfWork, IRequestContext requestContext)
{
    public Task<bool> LockTaskAsync(Guid id, CancellationToken cancellationToken) =>
        LockAsync("SELECT 1 FROM warehouse.picking_tasks WHERE tenant_id = @tenant AND id = @id FOR UPDATE", id, cancellationToken);

    public Task<bool> LockShipmentAsync(Guid id, CancellationToken cancellationToken) =>
        LockAsync("SELECT 1 FROM warehouse.shipments WHERE tenant_id = @tenant AND id = @id FOR UPDATE", id, cancellationToken);

    /// <summary>Every task, then every shipment of the order, each in id order (deterministic).</summary>
    public async Task LockOrderDocumentsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await LockAsync(
            """
            SELECT count(*) FROM (SELECT 1 FROM warehouse.picking_tasks WHERE tenant_id = @tenant AND order_id = @id ORDER BY id FOR UPDATE) t
            """,
            orderId,
            cancellationToken);
        await LockAsync(
            """
            SELECT count(*) FROM (SELECT 1 FROM warehouse.shipments WHERE tenant_id = @tenant AND order_id = @id ORDER BY id FOR UPDATE) s
            """,
            orderId,
            cancellationToken);
    }

    private async Task<bool> LockAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'; " + sql, unitOfWork.Connection, unitOfWork.Transaction);
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.") });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}

/// <summary>Opaque keyset cursor (api-strategy §5.1): base64url JSON of the last row's created_at and id.</summary>
internal sealed record FulfillmentCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
        new Payload(CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), Id)));

    public static FulfillmentCursor? TryDecode(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value)) is { T: not null, I: var id } payload
                && id != Guid.Empty
                && DateTimeOffset.TryParseExact(payload.T, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? new FulfillmentCursor(at, id)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Payload(string T, Guid I);
}

internal static class FulfillmentText
{
    public const int MaxCodeLength = 128;
    public const int MaxShortTextLength = 200;
    public const int MaxReasonLength = 500;
    public const int MaxLines = 500;
    public const int MaxPackages = 200;

    /// <summary>numeric(18,3): 15 integer digits.</summary>
    public const decimal MaxQuantity = 999_999_999_999_999m;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsQuantity(decimal quantity) =>
        quantity > 0 && quantity <= MaxQuantity && decimal.Round(quantity, 3) == quantity;
}
