using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>
/// What a warehouse_staff user's tasks expose to other modules (roles-permissions footnotes 5 and 8): orders behind
/// tasks assigned to them (any status but cancelled) or waiting in the active pool; variants on the lines of their
/// active assigned tasks and the active pool.
/// </summary>
internal sealed class TaskVisibility(WarehouseDbContext db) : IOrderTaskVisibility, ITaskContextVariants
{
    public async Task<IReadOnlyList<Guid>> OrdersVisibleToStaffAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.PickingTasks.AsNoTracking()
            .Where(t => (t.AssignedTo == userId && t.Status != PickingTaskStatuses.Cancelled)
                || (t.AssignedTo == null && PickingTaskStatuses.Active.Contains(t.Status)))
            .Select(t => t.OrderId)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> VariantsForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await (from line in db.PickingTaskLines.AsNoTracking()
               join task in db.PickingTasks.AsNoTracking() on line.PickingTaskId equals task.Id
               where PickingTaskStatuses.Active.Contains(task.Status) && (task.AssignedTo == userId || task.AssignedTo == null)
               select line.VariantId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
