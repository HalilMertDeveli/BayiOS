using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Orders.Domain;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Orders.Application;

/// <summary>
/// Orders a user created that can have warehouse documents (approved or later; drafts and pending orders have no
/// picking task). Tenant-filtered and RLS-bound like every Orders query.
/// </summary>
internal sealed class OrderOwnership(OrdersDbContext db) : IOrderOwnership
{
    private static readonly string[] WithoutTasks = [OrderStatuses.Draft, OrderStatuses.PendingApproval];

    public async Task<IReadOnlyList<Guid>> OrdersCreatedByAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking()
            .Where(o => o.CreatedBy == userId && !WithoutTasks.Contains(o.Status))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
}
