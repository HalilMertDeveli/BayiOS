using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Warehouse.Domain;

/// <summary>INV-06 / AF-06: open (counting) → submitted (fark raporu) → applied; recount sends it back to open; open/submitted → cancelled.</summary>
internal static class StockCountStatuses
{
    public const string Open = "open";
    public const string Submitted = "submitted";
    public const string Applied = "applied";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Open, Submitted, Applied, Cancelled];

    /// <summary>At most one active count per warehouse (partial unique index).</summary>
    public static readonly IReadOnlyList<string> Active = [Open, Submitted];
}

/// <summary>pending → counted → applied; a pending line is closed as uncounted when the count is applied.</summary>
internal static class StockCountLineStatuses
{
    public const string Pending = "pending";
    public const string Counted = "counted";
    public const string Applied = "applied";
    public const string Uncounted = "uncounted";

    public static readonly IReadOnlyList<string> All = [Pending, Counted, Applied, Uncounted];
}

/// <summary>full: every stocked row of the warehouse (and any item found there); locations / variants: only those.</summary>
internal static class StockCountScopes
{
    public const string Full = "full";
    public const string Locations = "locations";
    public const string Variants = "variants";

    public static readonly IReadOnlyList<string> All = [Full, Locations, Variants];
}

internal static class StockCountActions
{
    public const string Count = "count";
    public const string Submit = "submit";
    public const string Recount = "recount";
    public const string Apply = "apply";
    public const string Cancel = "cancel";
}

/// <summary>The single place that decides count transitions; null = 409 <c>count.invalid_transition</c>.</summary>
internal static class StockCountStateMachine
{
    public static string? Next(string action, string status) => (action, status) switch
    {
        (StockCountActions.Count, StockCountStatuses.Open) => StockCountStatuses.Open,
        (StockCountActions.Submit, StockCountStatuses.Open) => StockCountStatuses.Submitted,
        (StockCountActions.Recount, StockCountStatuses.Open or StockCountStatuses.Submitted) => StockCountStatuses.Open,
        (StockCountActions.Apply, StockCountStatuses.Submitted) => StockCountStatuses.Applied,
        (StockCountActions.Cancel, StockCountStatuses.Open or StockCountStatuses.Submitted) => StockCountStatuses.Cancelled,
        _ => null,
    };
}

/// <summary>warehouse.stock_counts (data-model §5.6, INV-06).</summary>
internal sealed class StockCount : WarehouseModuleEntity
{
    private StockCount()
    {
    }

    public string Number { get; private init; } = string.Empty;

    public Guid WarehouseId { get; private init; }

    public string Scope { get; private init; } = StockCountScopes.Full;

    /// <summary>Scope = locations: the counted shelves (other shelves cannot be added).</summary>
    public List<Guid> ScopeLocationIds { get; private init; } = [];

    /// <summary>Scope = variants: the counted variants.</summary>
    public List<Guid> ScopeVariantIds { get; private init; } = [];

    /// <summary>Kör sayım: counters without <c>inventory.count.manage</c> never see the system quantity.</summary>
    public bool IsBlind { get; private init; }

    public string Status { get; set; } = StockCountStatuses.Open;

    public string? Note { get; private init; }

    public DateTimeOffset SnapshotAt { get; private init; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public Guid? AppliedBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public List<StockCountLine> Lines { get; private init; } = [];

    public static StockCount Create(
        Guid tenantId,
        Guid warehouseId,
        string scope,
        IReadOnlyList<Guid> locationIds,
        IReadOnlyList<Guid> variantIds,
        bool isBlind,
        string? note,
        Guid userId,
        DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new StockCount
        {
            Id = id,
            TenantId = tenantId,
            Number = "SAY-" + id.ToString("N")[^8..].ToUpperInvariant(),
            WarehouseId = warehouseId,
            Scope = scope,
            ScopeLocationIds = [.. locationIds],
            ScopeVariantIds = [.. variantIds],
            IsBlind = isBlind,
            Note = note,
            SnapshotAt = now,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }

    public bool Covers(Guid variantId, Guid? locationId) => Scope switch
    {
        StockCountScopes.Locations => locationId is { } location && ScopeLocationIds.Contains(location),
        StockCountScopes.Variants => ScopeVariantIds.Contains(variantId),
        _ => true,
    };
}

/// <summary>
/// warehouse.stock_count_lines — one (variant, location) of a count. <see cref="ExpectedQuantity"/> is the system
/// on_hand at <see cref="SnapshotAt"/> (anlık görüntü); the correction is computed at apply time against the system
/// balance at <see cref="CountedAt"/> (inventory-and-orders §4.8).
/// </summary>
internal sealed class StockCountLine : ITenantOwned
{
    private StockCountLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid StockCountId { get; private init; }

    public Guid VariantId { get; private init; }

    public string Sku { get; private init; } = string.Empty;

    public Guid? LocationId { get; private init; }

    public decimal ExpectedQuantity { get; private init; }

    public DateTimeOffset SnapshotAt { get; private init; }

    public decimal? CountedQuantity { get; set; }

    public DateTimeOffset? CountedAt { get; set; }

    public Guid? CountedBy { get; set; }

    public string LineStatus { get; set; } = StockCountLineStatuses.Pending;

    /// <summary>The applied count_correction quantity (0 = no difference at apply time).</summary>
    public decimal? AppliedDelta { get; set; }

    public static StockCountLine Create(StockCount count, Guid variantId, string sku, Guid? locationId, decimal expected, DateTimeOffset snapshotAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = count.TenantId,
        StockCountId = count.Id,
        VariantId = variantId,
        Sku = sku,
        LocationId = locationId,
        ExpectedQuantity = expected,
        SnapshotAt = snapshotAt,
    };
}
