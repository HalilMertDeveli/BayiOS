using System.Text.Json;
using System.Text.RegularExpressions;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Catalog.Application;

internal sealed record CustomFieldPatch(
    Optional<string?> Label,
    Optional<string?> Unit,
    Optional<IReadOnlyList<string>?> Options,
    Optional<bool?> Required,
    Optional<bool?> Filterable,
    Optional<int?> SortOrder);

/// <summary>Custom field definitions (PRD CAT-05) and sector templates (PRD TEN-03).</summary>
internal sealed partial class CustomFieldService(CatalogDbContext db, IRequestContext requestContext, TimeProvider clock)
{
    /// <summary>Non-archived definitions per tenant; keeps forms and the validator small.</summary>
    public const int MaxActiveFields = 200;
    public const int MaxOptions = 100;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    // ---- Definitions ----

    public async Task<IReadOnlyList<CustomFieldResponse>> ListAsync(string? entityType, bool includeArchived, CancellationToken cancellationToken) =>
        (await db.CustomFields.AsNoTracking()
            .Where(f => (entityType == null || f.EntityType == entityType) && (includeArchived || f.ArchivedAt == null))
            .OrderBy(f => f.EntityType).ThenBy(f => f.SortOrder).ThenBy(f => f.Key)
            .Take(ReferenceDataService.MaxListSize)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    public async Task<AppResult<CustomFieldResponse>> CreateAsync(CreateCustomFieldRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var spec = ValidateDefinition(
            errors,
            request.EntityType,
            request.Key,
            request.Label,
            request.DataType,
            request.Unit,
            request.Options,
            request.SortOrder ?? 0);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (await db.CustomFields.AnyAsync(f => f.Key == spec.Key, cancellationToken))
        {
            return AppError.Conflict("catalog.field_key_taken", "Custom field key is already used in this tenant");
        }

        if (await db.CustomFields.CountAsync(f => f.ArchivedAt == null, cancellationToken) >= MaxActiveFields)
        {
            return AppError.Conflict("catalog.field_limit_reached", $"At most {MaxActiveFields} active custom fields per tenant");
        }

        var field = CustomFieldDefinition.Create(
            TenantId,
            spec.EntityType,
            spec.Key,
            spec.Label,
            spec.DataType,
            spec.Unit,
            JsonSerializer.Serialize(spec.Options),
            request.Required ?? false,
            request.Filterable ?? false,
            spec.SortOrder,
            UserId,
            CatalogText.Now(clock));
        db.CustomFields.Add(field);
        return await SaveAsync(cancellationToken) ?? (AppResult<CustomFieldResponse>)ToResponse(field);
    }

    public async Task<AppResult<CustomFieldResponse>> UpdateAsync(Guid id, CustomFieldPatch patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return AppError.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        if (patch.Required.IsSet && patch.Required.Value is null)
        {
            errors["required"] = ["Cannot be null."];
        }

        if (patch.Filterable.IsSet && patch.Filterable.Value is null)
        {
            errors["filterable"] = ["Cannot be null."];
        }

        if (patch.Label.IsSet && patch.Label.Value is null)
        {
            errors["label"] = ["Cannot be null."];
        }

        var spec = ValidateDefinition(
            errors,
            field.EntityType,
            field.Key,
            patch.Label.Value ?? field.Label,
            field.DataType,
            patch.Unit.IsSet ? patch.Unit.Value : field.Unit,
            patch.Options.IsSet ? patch.Options.Value : FieldSet.ReadOptions(field.Options),
            patch.SortOrder.IsSet ? patch.SortOrder.Value ?? 0 : field.SortOrder);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (field.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        field.Label = spec.Label;
        field.Unit = spec.Unit;
        field.Options = JsonSerializer.Serialize(spec.Options);
        field.SortOrder = spec.SortOrder;
        field.Required = patch.Required.Value ?? field.Required;
        field.Filterable = patch.Filterable.Value ?? field.Filterable;
        field.Touch(UserId, CatalogText.Now(clock));
        return await SaveAsync(cancellationToken) ?? (AppResult<CustomFieldResponse>)ToResponse(field);
    }

    /// <summary>
    /// DELETE = archive: the field stops being accepted on writes, required checks and filters; stored values stay
    /// in <c>attributes</c> and the key is never reused.
    /// </summary>
    public async Task<AppError?> ArchiveAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return AppError.NotFound();
        }

        if (field.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = CatalogText.Now(clock);
        field.ArchivedAt ??= now;
        field.Touch(UserId, now);
        return await SaveAsync(cancellationToken);
    }

    // ---- Sector templates ----

    /// <summary>
    /// Creates the template's units, root categories and custom fields that the tenant does not have yet. Existing
    /// rows (units by code incl. archived, non-archived root categories by name, fields by key incl. archived) are
    /// skipped and never modified, so re-applying is a no-op. A concurrent apply loses with 409 and can be retried.
    /// </summary>
    public async Task<AppResult<ApplySectorTemplateResponse>> ApplyTemplateAsync(string code, CancellationToken cancellationToken)
    {
        if (SectorTemplates.Find(code) is not { } template)
        {
            return AppError.NotFound();
        }

        var tenantId = TenantId;
        var userId = UserId;
        var now = CatalogText.Now(clock);
        var skipped = 0;

        var unitCodes = (await db.Units.AsNoTracking().Select(u => u.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var unitsCreated = new List<string>();
        foreach (var unit in template.Units)
        {
            if (!unitCodes.Add(unit.Code))
            {
                skipped++;
                continue;
            }

            db.Units.Add(Unit.Create(tenantId, unit.Code, unit.Name, unit.AllowsFraction, userId, now));
            unitsCreated.Add(unit.Code);
        }

        var rootNames = (await db.Categories.AsNoTracking()
                .Where(c => c.ParentId == null && c.ArchivedAt == null)
                .Select(c => c.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categoriesCreated = new List<string>();
        for (var i = 0; i < template.Categories.Count; i++)
        {
            var name = template.Categories[i];
            if (!rootNames.Add(name))
            {
                skipped++;
                continue;
            }

            db.Categories.Add(Category.CreateRoot(tenantId, name, (i + 1) * 10, userId, now));
            categoriesCreated.Add(name);
        }

        var keys = (await db.CustomFields.AsNoTracking().Select(f => f.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var activeCount = await db.CustomFields.CountAsync(f => f.ArchivedAt == null, cancellationToken);
        var fieldsCreated = new List<string>();
        for (var i = 0; i < template.Fields.Count; i++)
        {
            var f = template.Fields[i];
            if (!keys.Add(f.Key))
            {
                skipped++;
                continue;
            }

            if (activeCount + fieldsCreated.Count >= MaxActiveFields)
            {
                return AppError.Conflict("catalog.field_limit_reached", $"At most {MaxActiveFields} active custom fields per tenant");
            }

            db.CustomFields.Add(CustomFieldDefinition.Create(
                tenantId, f.EntityType, f.Key, f.Label, f.DataType, f.Unit, JsonSerializer.Serialize(f.Options), f.Required, f.Filterable, (i + 1) * 10, userId, now));
            fieldsCreated.Add(f.Key);
        }

        return await SaveAsync(cancellationToken)
            ?? (AppResult<ApplySectorTemplateResponse>)new ApplySectorTemplateResponse(template.Code, unitsCreated, categoriesCreated, fieldsCreated, skipped);
    }

    // ---- Validation ----

    internal sealed record DefinitionSpec(string EntityType, string Key, string Label, string DataType, string? Unit, IReadOnlyList<string> Options, int SortOrder);

    /// <summary>Definition rules shared by the API and the sector templates (a unit test runs every template through it).</summary>
    internal static DefinitionSpec ValidateDefinition(
        Dictionary<string, string[]> errors,
        string? entityType,
        string? key,
        string? label,
        string? dataType,
        string? unit,
        IReadOnlyList<string>? options,
        int sortOrder)
    {
        if (entityType is null || !CustomFieldEntityTypes.All.Contains(entityType))
        {
            errors["entityType"] = [$"Must be one of: {string.Join(", ", CustomFieldEntityTypes.All)}."];
        }

        if (key is null || !KeyPattern().IsMatch(key))
        {
            errors["key"] = ["Must match ^[a-z][a-z0-9_]{0,49}$ (lower-case ASCII letters, digits, underscore)."];
        }

        var cleanLabel = CatalogText.Clean(label);
        if (cleanLabel is null || cleanLabel.Length > 100)
        {
            errors.TryAdd("label", ["Must be 1–100 characters."]);
        }

        var type = dataType ?? string.Empty;
        if (!CustomFieldDataTypes.All.Contains(type))
        {
            errors["dataType"] = [$"Must be one of: {string.Join(", ", CustomFieldDataTypes.All)}."];
        }

        var cleanUnit = CatalogText.Clean(unit);
        if (cleanUnit is not null && (!CustomFieldDataTypes.HasUnit(type) || cleanUnit.Length > 20))
        {
            errors["unit"] = ["Only number, integer and range fields have a unit (1–20 characters)."];
        }

        var cleanOptions = (options ?? []).Select(o => CatalogText.Clean(o)).ToList();
        if (CustomFieldDataTypes.HasOptions(type))
        {
            if (cleanOptions.Count is 0 or > MaxOptions
                || cleanOptions.Any(o => o is null || o.Length > 100)
                || cleanOptions.Distinct(StringComparer.Ordinal).Count() != cleanOptions.Count)
            {
                errors["options"] = [$"enum and multi_enum need 1–{MaxOptions} distinct options of 1–100 characters."];
            }
        }
        else if (cleanOptions.Count > 0)
        {
            errors["options"] = ["Only enum and multi_enum fields have options."];
        }

        if (sortOrder is < 0 or > 10_000)
        {
            errors["sortOrder"] = ["Must be between 0 and 10000."];
        }

        return new DefinitionSpec(
            entityType ?? string.Empty,
            key ?? string.Empty,
            cleanLabel ?? string.Empty,
            type,
            cleanUnit,
            CustomFieldDataTypes.HasOptions(type) ? cleanOptions.Select(o => o ?? string.Empty).ToList() : [],
            sortOrder);
    }

    private async Task<AppError?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.VersionMismatch();
        }
        catch (DbUpdateException ex) when (AppError.FromUniqueViolation(ex) is { } conflict)
        {
            return conflict;
        }
    }

    internal static CustomFieldResponse ToResponse(CustomFieldDefinition f) => new(
        f.Id,
        f.EntityType,
        f.Key,
        f.Label,
        f.DataType,
        f.Unit,
        FieldSet.ReadOptions(f.Options),
        f.Required,
        f.Filterable,
        f.SortOrder,
        f.ArchivedAt is not null,
        f.RowVersion,
        f.CreatedAt,
        f.UpdatedAt);

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,49}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
