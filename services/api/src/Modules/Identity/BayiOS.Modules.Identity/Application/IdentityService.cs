using System.Text.RegularExpressions;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Domain;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Identity.Application;

/// <param name="Extensions">Extra Problem Details members (e.g. the id of a conflicting invitation).</param>
internal sealed record AppError(
    int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null, IReadOnlyDictionary<string, object?>? Extensions = null);

/// <summary>A merge-patch member: <see cref="IsSet"/> is false when absent from the document.</summary>
internal readonly record struct PatchValue(bool IsSet, string? Value);

internal sealed record TenantPatch(
    PatchValue Name,
    PatchValue Sector,
    PatchValue DefaultCurrency,
    PatchValue LogoObjectPath,
    PatchValue TaxNumber,
    PatchValue TaxOffice,
    PatchValue Phone);

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

internal sealed partial class IdentityService(
    IdentityDbContext db,
    IRequestContext requestContext,
    UnitOfWork unitOfWork,
    IAuditLog audit,
    IAuthorizationContext authorization,
    IEntitlements entitlements,
    ITenantSubscriptionProvisioner subscriptions,
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
                select new MembershipResponse(t.Id, t.Name, t.Slug, m.Role, m.Status, t.Status))
            .ToListAsync(cancellationToken);

        ActiveTenantResponse? active = null;
        if (requestContext is { TenantId: { } tenantId, Role: { } role })
        {
            var permissions = await authorization.EffectivePermissionsAsync(cancellationToken);
            var granted = await entitlements.CurrentAsync(cancellationToken);
            active = new ActiveTenantResponse(
                tenantId,
                role,
                [.. permissions.Select(p => new PermissionGrantResponse(
                    p.Permission,
                    p.Scope,
                    p.Condition,
                    p.Parameters is { } parameters ? new GrantParametersResponse(parameters.MaxDiscountPercent) : null))],
                new TenantEntitlementsResponse(
                    granted.Plan,
                    granted.Status,
                    granted.TrialEndsAt,
                    granted.CatalogVersion,
                    [.. granted.Features],
                    [.. granted.Limits.Select(l => new EntitlementLimitResponse(l.Key, l.Value))]));
        }

        return new MeResponse(userId, profile?.Email ?? tokenEmail, profile?.FullName, memberships, active);
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

        // PRD SUB-05: a new tenant starts on the trial (all modules open for the configured days), same transaction.
        await subscriptions.StartTrialAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(AuditActions.TenantCreated, "tenant", tenant.Id, new AuditChanges()
                .Value("slug", tenant.Slug)
                .Value("sector", tenant.Sector)
                .ToDictionary()),
            cancellationToken);
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
            errors["negativeStockPolicy"] = ["Must be block: stock never goes below zero (fixed rule)."];
        }

        if (request.SalesMaxDiscountPercent is < 0 or > 100)
        {
            errors["salesMaxDiscountPercent"] = ["Must be between 0 and 100."];
        }

        if (request.DefaultTaxRate is { } taxRate && (taxRate is < 0 or > 100 || decimal.Round(taxRate, 2) != taxRate))
        {
            errors["defaultTaxRate"] = ["Must be a percentage between 0 and 100 with at most 2 decimals."];
        }

        var prefix = request.OrderNumberPrefix?.Trim().ToUpperInvariant();
        if (request.OrderNumberPrefix is not null && !Domain.TenantSettings.IsValidOrderNumberPrefix(prefix))
        {
            errors["orderNumberPrefix"] = ["Must be 1–10 characters of A–Z and 0–9, starting with a letter."];
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

        var before = ToResponse(settings);
        settings.NegativeStockPolicy = NegativeStockPolicies.Block; // OD-09: the only accepted value (validated above)
        settings.SalesCanApproveOrders = request.SalesCanApproveOrders ?? settings.SalesCanApproveOrders;
        settings.SalesMaxDiscountPercent = request.SalesMaxDiscountPercent ?? settings.SalesMaxDiscountPercent;
        settings.ShowCostToManager = request.ShowCostToManager ?? settings.ShowCostToManager;
        settings.ShowCostToViewer = request.ShowCostToViewer ?? settings.ShowCostToViewer;
        settings.StaffCanAssignBarcode = request.StaffCanAssignBarcode ?? settings.StaffCanAssignBarcode;
        settings.StaffCanApproveReceipt = request.StaffCanApproveReceipt ?? settings.StaffCanApproveReceipt;
        settings.Require2fa = request.Require2fa ?? settings.Require2fa;
        settings.DefaultTaxRate = request.DefaultTaxRate ?? settings.DefaultTaxRate;
        settings.PricesIncludeTaxDisplay = request.PricesIncludeTaxDisplay ?? settings.PricesIncludeTaxDisplay;
        settings.RequireOrderApproval = request.RequireOrderApproval ?? settings.RequireOrderApproval;
        settings.OrderNumberPrefix = prefix ?? settings.OrderNumberPrefix;
        settings.MarkUpdated(UserId, clock.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return VersionMismatch();
        }

        var after = ToResponse(settings);

        // The adjustable rules of roles-permissions §4 (they change who may do what) get their own event.
        var rules = new AuditChanges()
            .Field("negativeStockPolicy", before.NegativeStockPolicy, after.NegativeStockPolicy)
            .Field("salesCanApproveOrders", before.SalesCanApproveOrders, after.SalesCanApproveOrders)
            .Field("salesMaxDiscountPercent", before.SalesMaxDiscountPercent, after.SalesMaxDiscountPercent)
            .Field("showCostToManager", before.ShowCostToManager, after.ShowCostToManager)
            .Field("showCostToViewer", before.ShowCostToViewer, after.ShowCostToViewer)
            .Field("staffCanAssignBarcode", before.StaffCanAssignBarcode, after.StaffCanAssignBarcode)
            .Field("staffCanApproveReceipt", before.StaffCanApproveReceipt, after.StaffCanApproveReceipt)
            .Field("require2fa", before.Require2fa, after.Require2fa);
        if (rules.HasChanges)
        {
            await audit.RecordAsync(
                new AuditEvent(AuditActions.TenantRulesChanged, "tenant_settings", settings.TenantId, rules.ToDictionary()),
                cancellationToken);
        }

        var changes = new AuditChanges()
            .Field("defaultTaxRate", before.DefaultTaxRate, after.DefaultTaxRate)
            .Field("pricesIncludeTaxDisplay", before.PricesIncludeTaxDisplay, after.PricesIncludeTaxDisplay)
            .Field("requireOrderApproval", before.RequireOrderApproval, after.RequireOrderApproval)
            .Field("orderNumberPrefix", before.OrderNumberPrefix, after.OrderNumberPrefix);
        if (changes.HasChanges)
        {
            await audit.RecordAsync(
                new AuditEvent(AuditActions.TenantSettingsChanged, "tenant_settings", settings.TenantId, changes.ToDictionary()),
                cancellationToken);
        }

        return (after, settings.RowVersion);
    }

    /// <summary>Business profile of the current tenant (PRD TEN-03/04/05); optimistic concurrency on tenants.row_version.</summary>
    public async Task<AppResult<TenantResponse>> UpdateTenantAsync(TenantPatch patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        var name = patch.Name.IsSet ? patch.Name.Value?.Trim() ?? string.Empty : tenant.Name;
        if (name.Length is < 2 or > 200)
        {
            errors["name"] = ["Name must be 2–200 characters."];
        }

        var sector = patch.Sector.IsSet ? patch.Sector.Value ?? string.Empty : tenant.Sector;
        if (!Sectors.All.Contains(sector))
        {
            errors["sector"] = [$"Sector must be one of: {string.Join(", ", Sectors.All)}."];
        }

        var currency = patch.DefaultCurrency.IsSet ? patch.DefaultCurrency.Value?.Trim().ToUpperInvariant() ?? string.Empty : tenant.DefaultCurrency;
        if (!CurrencyPattern().IsMatch(currency))
        {
            errors["defaultCurrency"] = ["Must be a 3-letter ISO 4217 currency code (e.g. TRY)."];
        }

        var logo = patch.LogoObjectPath.IsSet ? Clean(patch.LogoObjectPath.Value) : tenant.LogoObjectPath;
        if (logo is not null && !IsValidLogoPath(logo, tenantId))
        {
            errors["logoObjectPath"] = [$"Must be a storage path '{tenantId}/<name>.png|jpg|jpeg|svg' (≤ 512 characters, no '..')."];
        }

        var taxNumber = patch.TaxNumber.IsSet ? Clean(patch.TaxNumber.Value) : tenant.TaxNumber;
        if (taxNumber is not null && !TaxNumberPattern().IsMatch(taxNumber))
        {
            errors["taxNumber"] = ["Must be 10 (VKN) or 11 (TCKN) digits."];
        }

        var taxOffice = patch.TaxOffice.IsSet ? Clean(patch.TaxOffice.Value) : tenant.TaxOffice;
        if (taxOffice is { Length: > 100 })
        {
            errors["taxOffice"] = ["Must be at most 100 characters."];
        }

        var phone = patch.Phone.IsSet ? Clean(patch.Phone.Value) : tenant.Phone;
        if (phone is not null && !PhonePattern().IsMatch(phone))
        {
            errors["phone"] = ["Must be 7–20 characters of digits, spaces, '+', '-', '(' and ')'."];
        }

        if (errors.Count > 0)
        {
            return ValidationFailed(errors);
        }

        if (tenant.RowVersion != expectedVersion)
        {
            return VersionMismatch();
        }

        // Audited without personal data: tax number (may be a TCKN) and phone only as "changed" markers (data-policy §8).
        var changes = new AuditChanges()
            .Field("name", tenant.Name, name)
            .Field("sector", tenant.Sector, sector)
            .Field("defaultCurrency", tenant.DefaultCurrency, currency)
            .Field("logoObjectPath", tenant.LogoObjectPath, logo)
            .Redacted("taxNumber", tenant.TaxNumber, taxNumber)
            .Field("taxOffice", tenant.TaxOffice, taxOffice)
            .Redacted("phone", tenant.Phone, phone);
        tenant.UpdateProfile(name, sector, currency, logo, taxNumber, taxOffice, phone, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return VersionMismatch();
        }

        if (changes.HasChanges)
        {
            await audit.RecordAsync(
                new AuditEvent(AuditActions.TenantSettingsChanged, "tenant", tenant.Id, changes.ToDictionary()), cancellationToken);
        }

        return ToResponse(tenant, requestContext.Role!);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Tenant-prefixed object path (threat-model TB2→TB3); only the reference is stored here.</summary>
    private static bool IsValidLogoPath(string path, Guid tenantId) =>
        path.Length <= 512
        && path.StartsWith($"{tenantId:D}/", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains("//", StringComparison.Ordinal)
        && LogoPathPattern().IsMatch(path);

    private static AppError ValidationFailed(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    private static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    private static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    private static TenantResponse ToResponse(Tenant tenant, string role) => new(
        tenant.Id,
        tenant.Name,
        tenant.Slug,
        tenant.Status,
        tenant.Sector,
        tenant.DefaultCurrency,
        tenant.Locale,
        tenant.TimeZone,
        role,
        tenant.LogoObjectPath,
        tenant.TaxNumber,
        tenant.TaxOffice,
        tenant.Phone,
        tenant.RowVersion);

    private static TenantSettingsResponse ToResponse(TenantSettings s) => new(
        s.NegativeStockPolicy,
        s.SalesCanApproveOrders,
        s.SalesMaxDiscountPercent,
        s.ShowCostToManager,
        s.ShowCostToViewer,
        s.StaffCanAssignBarcode,
        s.StaffCanApproveReceipt,
        s.Require2fa,
        s.DefaultTaxRate,
        s.PricesIncludeTaxDisplay,
        s.RequireOrderApproval,
        s.OrderNumberPrefix);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[A-Z]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex(@"^[0-9a-f-]{36}/[A-Za-z0-9_\-./]+\.(png|jpg|jpeg|svg)\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogoPathPattern();

    [GeneratedRegex(@"^([0-9]{10}|[0-9]{11})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TaxNumberPattern();

    [GeneratedRegex(@"^[0-9+\-() ]{7,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
