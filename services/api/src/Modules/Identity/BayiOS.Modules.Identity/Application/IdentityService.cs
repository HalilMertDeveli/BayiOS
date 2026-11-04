using System.Text.RegularExpressions;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Domain;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Identity.Application;

internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null);

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

internal sealed partial class IdentityService(
    IdentityDbContext db,
    IRequestContext requestContext,
    UnitOfWork unitOfWork,
    TimeProvider clock)
{
    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public async Task<MeResponse> GetMeAsync(string? tokenEmail, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var memberships = await (
                from m in db.Memberships.AsNoTracking()
                join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
                where m.UserId == userId
                orderby t.Name
                select new MembershipResponse(t.Id, t.Name, t.Slug, m.Role, m.Status))
            .ToListAsync(cancellationToken);

        return new MeResponse(userId, profile?.Email ?? tokenEmail, profile?.FullName, memberships);
    }

    public async Task<AppResult<TenantResponse>> CreateTenantAsync(CreateTenantRequest request, string? tokenEmail, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var slug = request.Slug?.Trim() ?? string.Empty;
        var sector = string.IsNullOrWhiteSpace(request.Sector) ? Sectors.General : request.Sector.Trim();

        var errors = new Dictionary<string, string[]>();
        if (name.Length is < 2 or > 200)
        {
            errors["name"] = ["Name must be 2–200 characters."];
        }

        if (!SlugPattern().IsMatch(slug))
        {
            errors["slug"] = ["Slug must be 3–63 characters of a-z, 0-9 and '-', starting and ending with a letter or digit."];
        }

        if (!Sectors.All.Contains(sector))
        {
            errors["sector"] = [$"Sector must be one of: {string.Join(", ", Sectors.All)}."];
        }

        if (errors.Count > 0)
        {
            return ValidationFailed(errors);
        }

        var userId = UserId;
        var now = clock.GetUtcNow();
        var tenant = Tenant.Create(name, slug, sector, now);

        // RLS: the new tenant's rows are only writable inside its own context.
        await unitOfWork.SetTenantAsync(tenant.Id, cancellationToken);

        if (!await db.UserProfiles.AnyAsync(p => p.UserId == userId, cancellationToken))
        {
            db.UserProfiles.Add(UserProfile.Create(userId, tokenEmail, now));
        }

        db.Tenants.Add(tenant);
        db.Memberships.Add(Membership.CreateOwner(tenant.Id, userId, now));
        db.TenantSettings.Add(Domain.TenantSettings.CreateDefault(tenant.Id, userId, now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_tenants_slug" })
        {
            return new AppError(409, "tenant.slug_taken", "Slug is already in use");
        }

        return ToResponse(tenant, Roles.Owner);
    }

    public async Task<AppResult<TenantResponse>> GetCurrentTenantAsync(CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        return tenant is null ? NotFound() : ToResponse(tenant, requestContext.Role!);
    }

    public async Task<AppResult<(TenantSettingsResponse Settings, long Version)>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return settings is null ? NotFound() : (ToResponse(settings), settings.RowVersion);
    }

    public async Task<AppResult<(TenantSettingsResponse Settings, long Version)>> UpdateSettingsAsync(
        UpdateTenantSettingsRequest request,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.NegativeStockPolicy is { } policy && !NegativeStockPolicies.All.Contains(policy))
        {
            errors["negativeStockPolicy"] = [$"Must be one of: {string.Join(", ", NegativeStockPolicies.All)}."];
        }

        if (request.SalesMaxDiscountPercent is < 0 or > 100)
        {
            errors["salesMaxDiscountPercent"] = ["Must be between 0 and 100."];
        }

        if (errors.Count > 0)
        {
            return ValidationFailed(errors);
        }

        var settings = await db.TenantSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            return NotFound();
        }

        if (settings.RowVersion != expectedVersion)
        {
            return VersionMismatch();
        }

        settings.NegativeStockPolicy = request.NegativeStockPolicy ?? settings.NegativeStockPolicy;
        settings.SalesCanApproveOrders = request.SalesCanApproveOrders ?? settings.SalesCanApproveOrders;
        settings.SalesMaxDiscountPercent = request.SalesMaxDiscountPercent ?? settings.SalesMaxDiscountPercent;
        settings.ShowCostToManager = request.ShowCostToManager ?? settings.ShowCostToManager;
        settings.ShowCostToViewer = request.ShowCostToViewer ?? settings.ShowCostToViewer;
        settings.StaffCanAssignBarcode = request.StaffCanAssignBarcode ?? settings.StaffCanAssignBarcode;
        settings.StaffCanApproveReceipt = request.StaffCanApproveReceipt ?? settings.StaffCanApproveReceipt;
        settings.Require2fa = request.Require2fa ?? settings.Require2fa;
        settings.MarkUpdated(UserId, clock.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return VersionMismatch();
        }

        return (ToResponse(settings), settings.RowVersion);
    }

    private static AppError ValidationFailed(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    private static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    private static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    private static TenantResponse ToResponse(Tenant tenant, string role) => new(
        tenant.Id, tenant.Name, tenant.Slug, tenant.Status, tenant.Sector, tenant.DefaultCurrency, tenant.Locale, tenant.TimeZone, role);

    private static TenantSettingsResponse ToResponse(TenantSettings s) => new(
        s.NegativeStockPolicy,
        s.SalesCanApproveOrders,
        s.SalesMaxDiscountPercent,
        s.ShowCostToManager,
        s.ShowCostToViewer,
        s.StaffCanAssignBarcode,
        s.StaffCanApproveReceipt,
        s.Require2fa);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
