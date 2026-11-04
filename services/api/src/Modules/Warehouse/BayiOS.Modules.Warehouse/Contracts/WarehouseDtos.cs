namespace BayiOS.Modules.Warehouse.Contracts;

public sealed record WarehouseResponse(
    Guid Id,
    string Code,
    string Name,
    bool UsesLocations,
    bool IsDefault,
    string Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Unpaged reference list (≤ 1000, api-strategy §5.1).</summary>
public sealed record WarehouseListResponse(IReadOnlyList<WarehouseResponse> Items);

/// <param name="Code">1–20 characters, stored upper-case, unique per tenant, immutable.</param>
/// <param name="UsesLocations">Stock is kept per shelf/bin location (§2.6). Default false.</param>
public sealed record CreateWarehouseRequest(string? Code, string? Name, bool? UsesLocations);

/// <param name="UsesLocations">Can only change while the warehouse holds no stock.</param>
/// <param name="Status">active or inactive. The default warehouse cannot be deactivated.</param>
/// <param name="IsDefault">Only <c>true</c>: makes this the tenant's default warehouse.</param>
public sealed record UpdateWarehouseRequest(string? Name, bool? UsesLocations, string? Status, bool? IsDefault);

public sealed record LocationResponse(
    Guid Id,
    Guid WarehouseId,
    string Code,
    string? Barcode,
    int PickSequence,
    bool IsActive,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record LocationListResponse(IReadOnlyList<LocationResponse> Items);

/// <param name="Code">Shelf/bin code, unique within the warehouse (e.g. A-01-03).</param>
/// <param name="Barcode">Optional label barcode, unique per tenant.</param>
/// <param name="PickSequence">Walking order for picking/allocation (default 0).</param>
public sealed record CreateLocationRequest(string? Code, string? Barcode, int? PickSequence);

public sealed record UpdateLocationRequest(string? Code, string? Barcode, int? PickSequence, bool? IsActive);
