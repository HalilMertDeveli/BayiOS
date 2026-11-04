using BayiOS.Modules.Customers.Domain;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Customers.Application;

/// <summary>
/// Customers side of Pricing's <see cref="ICustomerPricingDirectory"/> port; same connection and transaction. An archived
/// dealer group contributes neither its list nor its discount.
/// </summary>
internal sealed class CustomerPricingDirectory(CustomersDbContext db) : ICustomerPricingDirectory
{
    public async Task<CustomerPricingProfile?> FindAsync(Guid customerId, CancellationToken cancellationToken) =>
        await (from customer in db.Customers.AsNoTracking()
               where customer.Id == customerId
               from grp in db.DealerGroups.AsNoTracking()
                   .Where(g => g.Id == customer.DealerGroupId && g.Status == DealerGroupStatuses.Active)
                   .DefaultIfEmpty()
               select new CustomerPricingProfile(
                   customer.Id,
                   customer.Status,
                   customer.Currency,
                   customer.PriceListId,
                   customer.DealerGroupId,
                   grp == null ? null : grp.PriceListId,
                   grp == null ? null : grp.DefaultDiscountPercent))
            .SingleOrDefaultAsync(cancellationToken);
}
