using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseModel = BayiOS.Modules.Warehouse.Domain.Warehouse;

namespace BayiOS.Modules.Warehouse.Application;

internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Conflict(string code, string title) => new(409, code, title);
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>
/// Warehouses and shelf/bin locations (data-model §5.6). The first warehouse of a tenant becomes its default;
/// receipts without a warehouse create the default ("Ana Depo", MAIN) on first use (<see cref="WarehouseDirectory"/>).
/// Entitlements (#51): active warehouses are capped by <c>max_warehouses</c> (Basic: 1; counted under a per-tenant
/// lock) and turning locations on needs <c>warehouse_locations</c> (Professional). Reads, renames, deactivation and
/// stock operations on existing warehouses are never gated, so a downgraded tenant keeps working with what it has.
/// </summary>
internal sealed class WarehouseService(
    WarehouseDbContext db, IStockQueries stock, IEntitlements entitlements, IRequestContext requestContext, TimeProvider clock)
{
    public const int MaxListSize = 1000;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    // ---- Warehouses ----

    public async Task<IReadOnlyList<WarehouseResponse>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        (await db.Warehouses.AsNoTracking()
            .Where(w => includeInactive || w.Status == WarehouseStatuses.Active)
            .OrderByDescending(w => w.IsDefault).ThenBy(w => w.Code)
            .Take(MaxListSize)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<AppResult<WarehouseResponse>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, cancellationToken) is { } warehouse
            ? ToResponse(warehouse)
            : AppError.NotFound();

    public async Task<AppResult<WarehouseResponse>> CreateAsync(CreateWarehouseRequest request, CancellationToken cancellationToken)
    {
        var code = NormalizeCode(request.Code);
        var name = Clean(request.Name);
        var errors = new Dictionary<string, string[]>();
        if (code is null || code.Length > 20)
        {
            errors["code"] = ["Code must be 1–20 characters without whitespace."];
        }

        ValidateName(errors, name);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (request.UsesLocations == true)
        {
            await entitlements.EnsureFeatureAsync(EntitlementCodes.WarehouseLocations, cancellationToken);
        }

        await EnsureWarehouseCapacityAsync(cancellationToken);
        var isDefault = !await db.Warehouses.AnyAsync(w => w.IsDefault, cancellationToken);
        var warehouse = WarehouseModel.Create(TenantId, code!, name!, request.UsesLocations ?? false, isDefault, UserId, Now());
        db.Warehouses.Add(warehouse);
        return await SaveAsync(() => ToResponse(warehouse), cancellationToken);
    }

    public async Task<AppResult<WarehouseResponse>> UpdateAsync(Guid id, UpdateWarehouseRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Name is not null)
        {
            ValidateName(errors, Clean(request.Name));
        }

        if (request.Status is not null && !WarehouseStatuses.All.Contains(request.Status))
        {
            errors["status"] = [$"Must be one of: {string.Join(", ", WarehouseStatuses.All)}."];
        }

        if (request.IsDefault == false)
        {
            errors["isDefault"] = ["Only true is accepted: make another warehouse the default instead."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (warehouse is null)
        {
            return AppError.NotFound();
        }

        if (warehouse.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var makeDefault = request.IsDefault == true && !warehouse.IsDefault;
        var status = request.Status ?? warehouse.Status;
        if (status != WarehouseStatuses.Active && (warehouse.IsDefault || makeDefault))
        {
            return AppError.Conflict("warehouse.default_inactive", "The default warehouse cannot be inactive");
        }

        if (request.UsesLocations is { } usesLocations && usesLocations != warehouse.UsesLocations
            && await stock.WarehouseHasStockAsync(warehouse.Id, cancellationToken))
        {
            return AppError.Conflict("warehouse.has_stock", "Location mode cannot change while the warehouse holds stock");
        }

        // Switching locations off or deactivating stays possible on any plan (way back from a downgrade).
        if (request.UsesLocations == true && !warehouse.UsesLocations)
        {
            await entitlements.EnsureFeatureAsync(EntitlementCodes.WarehouseLocations, cancellationToken);
        }

        if (status == WarehouseStatuses.Active && warehouse.Status != WarehouseStatuses.Active)
        {
            await EnsureWarehouseCapacityAsync(cancellationToken);
        }

        var now = Now();
        if (makeDefault)
        {
            // Clear the old default first: the partial unique index allows one default per tenant at any time.
            var current = await db.Warehouses.SingleOrDefaultAsync(w => w.IsDefault, cancellationToken);
            if (current is not null)
            {
                current.IsDefault = false;
                current.Touch(UserId, now);
                if ((await SaveAsync(() => true, cancellationToken)).Error is { } error)
                {
                    return error;
                }
            }

            warehouse.IsDefault = true;
        }

        warehouse.Name = Clean(request.Name) ?? warehouse.Name;
        warehouse.UsesLocations = request.UsesLocations ?? warehouse.UsesLocations;
        warehouse.Status = status;
        warehouse.Touch(UserId, now);
        return await SaveAsync(() => ToResponse(warehouse), cancellationToken);
    }

    // ---- Locations ----

    public async Task<AppResult<IReadOnlyList<LocationResponse>>> ListLocationsAsync(Guid warehouseId, bool includeInactive, CancellationToken cancellationToken)
    {
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, cancellationToken))
        {
            return AppError.NotFound();
        }

        return (await db.Locations.AsNoTracking()
                .Where(l => l.WarehouseId == warehouseId && (includeInactive || l.IsActive))
                .OrderBy(l => l.PickSequence).ThenBy(l => l.Code)
                .Take(MaxListSize)
                .ToListAsync(cancellationToken))
            .Select(ToResponse)
            .ToList();
    }

    public async Task<AppResult<LocationResponse>> CreateLocationAsync(Guid warehouseId, CreateLocationRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var code = NormalizeCode(request.Code);
        var barcode = Clean(request.Barcode);
        ValidateLocation(errors, code, barcode, request.PickSequence);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken);
        if (warehouse is null)
        {
            return AppError.NotFound();
        }

        if (!warehouse.UsesLocations)
        {
            return AppError.Conflict("warehouse.locations_disabled", "This warehouse does not use locations");
        }

        var location = WarehouseLocation.Create(TenantId, warehouseId, code!, barcode, request.PickSequence ?? 0, UserId, Now());
        db.Locations.Add(location);
        return await SaveAsync(() => ToResponse(location), cancellationToken);
    }

    public async Task<AppResult<LocationResponse>> UpdateLocationAsync(Guid id, UpdateLocationRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var code = request.Code is null ? null : NormalizeCode(request.Code);
        if (request.Code is not null && (code is null || code.Length > 50))
        {
            errors["code"] = ["Code must be 1–50 characters without whitespace."];
        }

        ValidateLocation(errors, null, Clean(request.Barcode), request.PickSequence);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var location = await db.Locations.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (location is null)
        {
            return AppError.NotFound();
        }

        if (location.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        location.Code = code ?? location.Code;
        location.Barcode = request.Barcode is null ? location.Barcode : Clean(request.Barcode);
        location.PickSequence = request.PickSequence ?? location.PickSequence;
        location.IsActive = request.IsActive ?? location.IsActive;
        location.Touch(UserId, Now());
        return await SaveAsync(() => ToResponse(location), cancellationToken);
    }

    // ---- Helpers ----

    /// <summary>
    /// 403 <c>entitlement.limit_exceeded</c> when one more active warehouse would exceed <c>max_warehouses</c>. A
    /// per-tenant transaction lock serializes concurrent creates/reactivations so the count cannot be overshot.
    /// </summary>
    private async Task EnsureWarehouseCapacityAsync(CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var tenant = TenantId.ToString();
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('warehouse.capacity:' || {tenant}, 0))", cancellationToken);
        var active = await db.Warehouses.CountAsync(w => w.Status == WarehouseStatuses.Active, cancellationToken);
        await entitlements.EnsureCapacityAsync(EntitlementCodes.MaxWarehouses, active, cancellationToken);
    }

    private async Task<AppResult<T>> SaveAsync<T>(Func<T> response, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.VersionMismatch();
        }
        catch (DbUpdateException ex) when (FromUniqueViolation(ex) is { } conflict)
        {
            return conflict;
        }

        return response();
    }

    private static AppError? FromUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            ? pg.ConstraintName switch
            {
                WarehouseDbContext.CodeUniqueIndex => AppError.Conflict("warehouse.code_taken", "Warehouse code is already used in this tenant"),
                WarehouseDbContext.DefaultUniqueIndex => AppError.Conflict("warehouse.default_conflict", "Another default warehouse was set concurrently; retry"),
                WarehouseDbContext.LocationCodeUniqueIndex => AppError.Conflict("warehouse.location_code_taken", "Location code is already used in this warehouse"),
                WarehouseDbContext.LocationBarcodeUniqueIndex => AppError.Conflict("warehouse.location_barcode_taken", "Location barcode is already used in this tenant"),
                _ => null,
            }
            : null;

    private static void ValidateName(Dictionary<string, string[]> errors, string? name)
    {
        if (name is null || name.Length > 100)
        {
            errors["name"] = ["Must be 1–100 characters."];
        }
    }

    private static void ValidateLocation(Dictionary<string, string[]> errors, string? code, string? barcode, int? pickSequence)
    {
        if (code is not null && code.Length > 50)
        {
            errors["code"] = ["Code must be 1–50 characters without whitespace."];
        }

        if (barcode is not null && (barcode.Length > 64 || barcode.Any(char.IsWhiteSpace)))
        {
            errors["barcode"] = ["Must be 1–64 characters without whitespace."];
        }

        if (pickSequence is < 0 or > 1_000_000)
        {
            errors["pickSequence"] = ["Must be between 0 and 1000000."];
        }
    }

    /// <summary>Trimmed, upper-cased (invariant) code; null when blank or containing whitespace/control characters.</summary>
    internal static string? NormalizeCode(string? code)
    {
        var trimmed = Clean(code);
        return trimmed is null || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : trimmed.ToUpperInvariant();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Microsecond precision: what PostgreSQL <c>timestamptz</c> stores.</summary>
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    private static WarehouseResponse ToResponse(WarehouseModel w) =>
        new(w.Id, w.Code, w.Name, w.UsesLocations, w.IsDefault, w.Status, w.RowVersion, w.CreatedAt, w.UpdatedAt);

    private static LocationResponse ToResponse(WarehouseLocation l) =>
        new(l.Id, l.WarehouseId, l.Code, l.Barcode, l.PickSequence, l.IsActive, l.RowVersion, l.CreatedAt, l.UpdatedAt);
}
