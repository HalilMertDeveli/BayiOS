using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Catalog.Application;

/// <summary>Units, brands and categories: small per-tenant reference lists (unpaged, ≤ 1000 rows).</summary>
internal sealed class ReferenceDataService(CatalogDbContext db, IRequestContext requestContext, TimeProvider clock)
{
    public const int MaxListSize = 1000;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    // ---- Units ----

    public async Task<IReadOnlyList<UnitResponse>> ListUnitsAsync(bool includeArchived, CancellationToken cancellationToken) =>
        (await db.Units.AsNoTracking()
            .Where(u => includeArchived || u.ArchivedAt == null)
            .OrderBy(u => u.Code)
            .Take(MaxListSize)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<AppResult<UnitResponse>> CreateUnitAsync(CreateUnitRequest request, CancellationToken cancellationToken)
    {
        var code = CatalogCodes.NormalizeUnitCode(request.Code ?? string.Empty);
        var name = CatalogText.Clean(request.Name);
        var errors = new Dictionary<string, string[]>();
        if (!CatalogCodes.IsValidCode(code) || code.Length > 20)
        {
            errors["code"] = ["Code must be 1–20 characters without whitespace."];
        }

        ValidateName(errors, "name", name, 50);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var unit = Unit.Create(TenantId, code, name!, request.AllowsFraction ?? false, UserId, CatalogText.Now(clock));
        db.Units.Add(unit);
        return await SaveAsync(ToResponse(unit), cancellationToken);
    }

    public async Task<AppResult<UnitResponse>> UpdateUnitAsync(Guid id, UpdateUnitRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Name is not null)
        {
            ValidateName(errors, "name", CatalogText.Clean(request.Name), 50);
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var unit = await db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
        {
            return AppError.NotFound();
        }

        if (unit.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        unit.Name = CatalogText.Clean(request.Name) ?? unit.Name;
        unit.AllowsFraction = request.AllowsFraction ?? unit.AllowsFraction;
        unit.Touch(UserId, CatalogText.Now(clock));
        return await SaveAsync(ToResponse(unit), cancellationToken);
    }

    public async Task<AppError?> ArchiveUnitAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var unit = await db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
        {
            return AppError.NotFound();
        }

        if (unit.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = CatalogText.Now(clock);
        unit.ArchivedAt ??= now;
        unit.Touch(UserId, now);
        return (await SaveAsync(true, cancellationToken)).Error;
    }

    // ---- Brands ----

    public async Task<IReadOnlyList<BrandResponse>> ListBrandsAsync(bool includeArchived, string? q, CancellationToken cancellationToken)
    {
        var query = db.Brands.AsNoTracking().Where(b => includeArchived || b.ArchivedAt == null);
        if (CatalogText.Clean(q) is { } term)
        {
            var pattern = CatalogText.ContainsPattern(term);
            query = query.Where(b => EF.Functions.ILike(b.Name, pattern, "\\"));
        }

        return (await query.OrderBy(b => b.Name).ThenBy(b => b.Id).Take(MaxListSize).ToListAsync(cancellationToken))
            .Select(ToResponse)
            .ToList();
    }

    public async Task<AppResult<BrandResponse>> CreateBrandAsync(CreateBrandRequest request, CancellationToken cancellationToken)
    {
        var name = CatalogText.Clean(request.Name);
        var errors = new Dictionary<string, string[]>();
        ValidateName(errors, "name", name, 100);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var brand = Brand.Create(TenantId, name!, UserId, CatalogText.Now(clock));
        db.Brands.Add(brand);
        return await SaveAsync(ToResponse(brand), cancellationToken);
    }

    public async Task<AppResult<BrandResponse>> UpdateBrandAsync(Guid id, UpdateBrandRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Name is not null)
        {
            ValidateName(errors, "name", CatalogText.Clean(request.Name), 100);
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var brand = await db.Brands.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (brand is null)
        {
            return AppError.NotFound();
        }

        if (brand.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        brand.Name = CatalogText.Clean(request.Name) ?? brand.Name;
        brand.Touch(UserId, CatalogText.Now(clock));
        return await SaveAsync(ToResponse(brand), cancellationToken);
    }

    public async Task<AppError?> ArchiveBrandAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (brand is null)
        {
            return AppError.NotFound();
        }

        if (brand.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = CatalogText.Now(clock);
        brand.ArchivedAt ??= now;
        brand.Touch(UserId, now);
        return (await SaveAsync(true, cancellationToken)).Error;
    }

    // ---- Categories ----

    public async Task<IReadOnlyList<CategoryResponse>> ListCategoriesAsync(bool includeArchived, CancellationToken cancellationToken) =>
        (await db.Categories.AsNoTracking()
            .Where(c => includeArchived || c.ArchivedAt == null)
            .OrderBy(c => c.Depth).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Take(MaxListSize)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<AppResult<CategoryResponse>> CreateCategoryAsync(CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        var name = CatalogText.Clean(request.Name);
        var errors = new Dictionary<string, string[]>();
        ValidateName(errors, "name", name, 100);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var now = CatalogText.Now(clock);
        var sortOrder = request.SortOrder ?? 0;
        Category category;
        if (request.ParentId is { } parentId)
        {
            var parent = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parentId && c.ArchivedAt == null, cancellationToken);
            if (parent is null)
            {
                return AppError.Validation(new Dictionary<string, string[]> { ["parentId"] = ["Parent category not found."] });
            }

            var child = parent.CreateChild(name!, sortOrder, UserId, now);
            if (child is null)
            {
                return AppError.Validation(new Dictionary<string, string[]> { ["parentId"] = [$"Category tree depth is limited to {Category.MaxDepth}."] });
            }

            category = child;
        }
        else
        {
            category = Category.CreateRoot(TenantId, name!, sortOrder, UserId, now);
        }

        db.Categories.Add(category);
        return await SaveAsync(ToResponse(category), cancellationToken);
    }

    public async Task<AppResult<CategoryResponse>> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Name is not null)
        {
            ValidateName(errors, "name", CatalogText.Clean(request.Name), 100);
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return AppError.NotFound();
        }

        if (category.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        category.Name = CatalogText.Clean(request.Name) ?? category.Name;
        category.SortOrder = request.SortOrder ?? category.SortOrder;
        category.Touch(UserId, CatalogText.Now(clock));
        return await SaveAsync(ToResponse(category), cancellationToken);
    }

    public async Task<AppError?> ArchiveCategoryAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return AppError.NotFound();
        }

        if (category.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        if (await db.Categories.AnyAsync(c => c.ParentId == id && c.ArchivedAt == null, cancellationToken))
        {
            return AppError.Conflict("catalog.category_has_children", "Archive the subcategories first");
        }

        var now = CatalogText.Now(clock);
        category.ArchivedAt ??= now;
        category.Touch(UserId, now);
        return (await SaveAsync(true, cancellationToken)).Error;
    }

    private async Task<AppResult<T>> SaveAsync<T>(T response, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.VersionMismatch();
        }
        catch (DbUpdateException ex) when (AppError.FromUniqueViolation(ex) is { } conflict)
        {
            return conflict;
        }

        return response;
    }

    private static void ValidateName(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        if (value is null || value.Length > maxLength)
        {
            errors[field] = [$"Must be 1–{maxLength} characters."];
        }
    }

    private static UnitResponse ToResponse(Unit u) =>
        new(u.Id, u.Code, u.Name, u.AllowsFraction, u.ArchivedAt is not null, u.RowVersion, u.CreatedAt, u.UpdatedAt);

    private static BrandResponse ToResponse(Brand b) =>
        new(b.Id, b.Name, b.ArchivedAt is not null, b.RowVersion, b.CreatedAt, b.UpdatedAt);

    private static CategoryResponse ToResponse(Category c) =>
        new(c.Id, c.ParentId, c.Name, c.Path, c.Depth, c.SortOrder, c.ArchivedAt is not null, c.RowVersion, c.CreatedAt, c.UpdatedAt);
}
