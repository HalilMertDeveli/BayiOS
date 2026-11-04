using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Customers.Domain;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Customers.Application;

/// <summary>
/// Dealer (price) groups (CUS-02): a group carries the sales price list its customers get. Managing groups needs
/// <c>customers.groups.manage</c>; assigning a price list additionally needs <c>pricelists.assign</c>.
/// </summary>
internal sealed class DealerGroupService(
    CustomersDbContext db,
    IPriceListDirectory priceLists,
    IRequestContext requestContext,
    TimeProvider clock)
{
    public const int MaxListSize = 1000;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public async Task<IReadOnlyList<DealerGroupResponse>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        (await db.DealerGroups.AsNoTracking()
            .Where(g => includeArchived || g.Status == DealerGroupStatuses.Active)
            .OrderBy(g => g.Name).ThenBy(g => g.Id)
            .Take(MaxListSize)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<AppResult<DealerGroupResponse>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.DealerGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken) is { } group
            ? ToResponse(group)
            : AppError.NotFound();

    public async Task<AppResult<DealerGroupResponse>> CreateAsync(CreateDealerGroupRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = CustomerText.Clean(request.Name);
        ValidateName(errors, name);
        ValidateDiscount(errors, request.DefaultDiscountPercent);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (request.PriceListId is { } priceListId && await CheckPriceListAsync(priceListId, cancellationToken) is { } priceListError)
        {
            return priceListError;
        }

        var group = DealerGroup.Create(TenantId, name!, request.PriceListId, request.DefaultDiscountPercent, UserId, Now());
        db.DealerGroups.Add(group);
        return await SaveAsync(() => ToResponse(group), cancellationToken);
    }

    public async Task<AppResult<DealerGroupResponse>> UpdateAsync(
        Guid id, Patch<UpdateDealerGroupRequest> patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var values = patch.Values;
        var errors = new Dictionary<string, string[]>();
        var name = CustomerText.Clean(values.Name);
        if (patch.Has("name"))
        {
            ValidateName(errors, name);
        }

        if (patch.Has("status") && (values.Status is null || !DealerGroupStatuses.All.Contains(values.Status)))
        {
            errors["status"] = [$"Must be one of: {string.Join(", ", DealerGroupStatuses.All)}."];
        }

        ValidateDiscount(errors, values.DefaultDiscountPercent);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var group = await db.DealerGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return AppError.NotFound();
        }

        if (group.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        if (patch.Has("priceListId") && values.PriceListId != group.PriceListId)
        {
            if (values.PriceListId is { } priceListId && await CheckPriceListAsync(priceListId, cancellationToken) is { } priceListError)
            {
                return priceListError;
            }

            if (values.PriceListId is null && !RolePermissions.IsGranted(requestContext.Role, Permissions.PricelistsAssign))
            {
                return AppError.Forbidden(Permissions.PricelistsAssign);
            }

            group.PriceListId = values.PriceListId;
        }

        var now = Now();
        group.Name = name ?? group.Name;
        if (patch.Has("defaultDiscountPercent"))
        {
            group.DefaultDiscountPercent = values.DefaultDiscountPercent;
        }

        group.SetStatus(values.Status ?? group.Status, now);
        group.Touch(UserId, now);
        return await SaveAsync(() => ToResponse(group), cancellationToken);
    }

    /// <summary>Assigning needs <c>pricelists.assign</c>; the list must be a sales list that is not archived.</summary>
    private async Task<AppError?> CheckPriceListAsync(Guid priceListId, CancellationToken cancellationToken)
    {
        if (!RolePermissions.IsGranted(requestContext.Role, Permissions.PricelistsAssign))
        {
            return AppError.Forbidden(Permissions.PricelistsAssign);
        }

        return await priceLists.FindAsync(priceListId, cancellationToken) is { IsAssignable: true }
            ? null
            : AppError.Validation(new Dictionary<string, string[]> { ["priceListId"] = ["Must be a sales price list that is not archived."] });
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CustomersDbContext.GroupNameUniqueIndex,
        })
        {
            return AppError.Conflict("customers.group_name_taken", "A dealer group with this name already exists");
        }

        return response();
    }

    private static void ValidateName(Dictionary<string, string[]> errors, string? name)
    {
        if (name is null || name.Length > 100)
        {
            errors["name"] = ["Must be 1–100 characters."];
        }
    }

    private static void ValidateDiscount(Dictionary<string, string[]> errors, decimal? discount)
    {
        if (discount is { } percent && !CustomerText.IsPercent(percent))
        {
            errors["defaultDiscountPercent"] = ["Must be between 0 and 100 with at most 2 decimals."];
        }
    }

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    internal static DealerGroupResponse ToResponse(DealerGroup g) =>
        new(g.Id, g.Name, g.PriceListId, g.DefaultDiscountPercent, g.Status, g.RowVersion, g.CreatedAt, g.UpdatedAt);
}
