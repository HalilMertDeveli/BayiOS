using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Warehouse.Domain;

internal static class WarehouseStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";

    public static readonly IReadOnlyList<string> All = [Active, Inactive];
}

/// <summary>Shared columns (data-model §1): tenant, audit stamps, optimistic concurrency (ETag source).</summary>
internal abstract class WarehouseModuleEntity : ITenantOwned
{
    public Guid Id { get; protected init; }

    public Guid TenantId { get; protected init; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; protected init; }

    public Guid? CreatedBy { get; protected init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Touch(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>warehouse.warehouses (data-model §5.6). Exactly one default per tenant once any exists.</summary>
internal sealed class Warehouse : WarehouseModuleEntity
{
    public const string DefaultCode = "MAIN";
    public const string DefaultName = "Ana Depo";

    private Warehouse()
    {
    }

    public string Code { get; private init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>false → one location-less balance row per variant; true → stock lives on locations (§2.6).</summary>
    public bool UsesLocations { get; set; }

    public bool IsDefault { get; set; }

    public string Status { get; set; } = WarehouseStatuses.Active;

    public static Warehouse Create(Guid tenantId, string code, string name, bool usesLocations, bool isDefault, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Code = code,
        Name = name,
        UsesLocations = usesLocations,
        IsDefault = isDefault,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>warehouse.warehouse_locations — shelf/bin inside a warehouse.</summary>
internal sealed class WarehouseLocation : WarehouseModuleEntity
{
    private WarehouseLocation()
    {
    }

    public Guid WarehouseId { get; private init; }

    public string Code { get; set; } = string.Empty;

    public string? Barcode { get; set; }

    /// <summary>Walking order for picking and reservation allocation (§4.3).</summary>
    public int PickSequence { get; set; }

    public bool IsActive { get; set; } = true;

    public static WarehouseLocation Create(
        Guid tenantId, Guid warehouseId, string code, string? barcode, int pickSequence, Guid userId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            Code = code,
            Barcode = barcode,
            PickSequence = pickSequence,
            CreatedAt = now,
            CreatedBy = userId,
        };
}
