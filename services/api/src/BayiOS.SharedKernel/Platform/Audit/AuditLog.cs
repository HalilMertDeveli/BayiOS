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
    public const string InventoryReceiptPosted = "inventory.receipt_posted";
    public const string InventoryAdjustmentPosted = "inventory.adjustment_posted";

    /// <summary>A stock change that went below zero after an explicit confirmation under the <c>warn</c> policy.</summary>
    public const string InventoryNegativeStockConfirmed = "inventory.negative_stock_confirmed";
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
