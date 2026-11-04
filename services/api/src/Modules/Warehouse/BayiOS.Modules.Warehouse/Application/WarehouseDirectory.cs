using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WarehouseModel = BayiOS.Modules.Warehouse.Domain.Warehouse;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>Warehouse side of Inventory's <see cref="IWarehouseDirectory"/> port; same connection and transaction.</summary>
internal sealed class WarehouseDirectory(WarehouseDbContext db, UnitOfWork unitOfWork, IRequestContext requestContext) : IWarehouseDirectory
{
    public async Task<WarehouseInfo?> FindWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken) =>
        await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == warehouseId)
            .Select(w => new WarehouseInfo(w.Id, w.Code, w.Status == WarehouseStatuses.Active, w.UsesLocations))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Default warehouse on first use: <c>INSERT … WHERE NOT EXISTS … ON CONFLICT DO NOTHING</c>, so concurrent first
    /// receipts of a tenant converge on one default (the partial unique index decides) instead of failing.
    /// </summary>
    public async Task<WarehouseInfo> GetOrCreateDefaultAsync(CancellationToken cancellationToken)
    {
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");
        var transaction = unitOfWork.Transaction ?? throw new InvalidOperationException("The unit of work has not started.");

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO warehouse.warehouses (id, tenant_id, code, name, uses_locations, is_default, status, row_version, created_at, created_by)
            SELECT @id, @tenant, @code, @name, false, true, 'active', 1, now(), @user
            WHERE NOT EXISTS (SELECT 1 FROM warehouse.warehouses WHERE tenant_id = @tenant AND is_default)
            ON CONFLICT DO NOTHING
            """,
            unitOfWork.Connection,
            transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.CreateVersion7() });
            insert.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = tenantId });
            insert.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Text) { Value = WarehouseModel.DefaultCode });
            insert.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = WarehouseModel.DefaultName });
            insert.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)requestContext.UserId ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return await db.Warehouses.AsNoTracking()
            .Where(w => w.IsDefault)
            .Select(w => new WarehouseInfo(w.Id, w.Code, w.Status == WarehouseStatuses.Active, w.UsesLocations))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The tenant has warehouses but no default one.");
    }

    public async Task<IReadOnlyDictionary<Guid, LocationInfo>> FindLocationsAsync(IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken) =>
        await db.Locations.AsNoTracking()
            .Where(l => locationIds.Contains(l.Id))
            .Select(l => new LocationInfo(l.Id, l.WarehouseId, l.IsActive, l.PickSequence))
            .ToDictionaryAsync(l => l.Id, cancellationToken);
}
