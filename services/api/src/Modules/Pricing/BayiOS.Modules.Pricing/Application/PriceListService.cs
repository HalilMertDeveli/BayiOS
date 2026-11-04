using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Domain;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Pricing.Application;

/// <summary>Merge-patch view: which members were present in the body (absent = unchanged, null = clear).</summary>
internal sealed record PriceListPatch(UpdatePriceListRequest Values, IReadOnlySet<string> Present)
{
    public bool Has(string member) => Present.Contains(member);
}

/// <summary>
/// Price lists and their items (data-model §5.4, CAT-08). Purchase lists exist for cost prices and are hidden from
/// callers without an unconditional <c>prices.read.cost</c> grant (api-strategy field-level rule). A derived list
/// (base + discount) must use a base that is itself not derived: one level only, so no chains or cycles.
/// </summary>
internal sealed class PriceListService(
    PricingDbContext db,
    IPricingVariantDirectory variants,
    ITenantCurrencyProvider tenantCurrency,
    IRequestContext requestContext,
    TimeProvider clock)
{
    public const int MaxListSize = 1000;
    public const int MaxItemsPerUpsert = 500;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private bool CanReadCost => RolePermissions.IsGranted(requestContext.Role, Permissions.PricesReadCost);

    // ---- Price lists ----

    public async Task<IReadOnlyList<PriceListResponse>> ListAsync(bool includeArchived, CancellationToken cancellationToken)
    {
        var canReadCost = CanReadCost;
        return (await db.PriceLists.AsNoTracking()
                .Where(l => includeArchived || l.Status != PriceListStatuses.Archived)
                .Where(l => canReadCost || l.Kind == PriceListKinds.Sales)
                .OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).ThenBy(l => l.Id)
                .Take(MaxListSize)
                .ToListAsync(cancellationToken))
            .Select(ToResponse)
            .ToList();
    }

    public async Task<AppResult<PriceListResponse>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await FindVisibleAsync(id, tracked: false, cancellationToken) is { } list ? ToResponse(list) : AppError.NotFound();

    public async Task<AppResult<PriceListResponse>> CreateAsync(CreatePriceListRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = PricingNumbers.Clean(request.Name);
        ValidateName(errors, name);
        var kind = PricingNumbers.Clean(request.Kind) ?? PriceListKinds.Sales;
        if (!PriceListKinds.All.Contains(kind))
        {
            errors["kind"] = [$"Must be one of: {string.Join(", ", PriceListKinds.All)}."];
        }

        var currency = PricingNumbers.NormalizeCurrency(request.Currency, out var currencyValid);
        if (!currencyValid)
        {
            errors["currency"] = ["Must be a 3-letter ISO 4217 code."];
        }

        ValidateValidity(errors, request.ValidFrom, request.ValidTo);
        ValidateRule(errors, request.BasePriceListId, request.DiscountPercent);
        if (kind == PriceListKinds.Purchase && request.IsDefault == true)
        {
            errors["isDefault"] = ["Only sales lists can be the default."];
        }

        if (kind == PriceListKinds.Purchase && request.BasePriceListId is not null)
        {
            errors["basePriceListId"] = ["Only sales lists can derive from a base list."];
        }

        if (request.IsDefault == false)
        {
            errors["isDefault"] = ["Only true is accepted."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        currency ??= await tenantCurrency.GetDefaultCurrencyAsync(cancellationToken);
        var pricesIncludeTax = request.PricesIncludeTax ?? false;
        if (request.BasePriceListId is { } baseId
            && await CheckBaseAsync(null, baseId, currency, pricesIncludeTax, cancellationToken) is { } baseError)
        {
            return baseError;
        }

        var now = Now();
        var list = PriceList.Create(
            TenantId, name!, kind, currency, pricesIncludeTax, request.ValidFrom, request.ValidTo, request.BasePriceListId, request.DiscountPercent, UserId, now);
        if (kind == PriceListKinds.Sales)
        {
            var current = await db.PriceLists
                .SingleOrDefaultAsync(l => l.IsDefault && l.Kind == PriceListKinds.Sales && l.Currency == currency, cancellationToken);
            if (current is null)
            {
                // The first sales list of a currency becomes its default (resolution step 3 always has a list).
                list.IsDefault = true;
            }
            else if (request.IsDefault == true && await ClearDefaultAsync(current, now, cancellationToken) is { } clearError)
            {
                return clearError;
            }
            else if (request.IsDefault == true)
            {
                list.IsDefault = true;
            }
        }

        db.PriceLists.Add(list);
        return await SaveAsync(() => ToResponse(list), cancellationToken);
    }

    public async Task<AppResult<PriceListResponse>> UpdateAsync(Guid id, PriceListPatch patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var values = patch.Values;
        var errors = new Dictionary<string, string[]>();
        var name = PricingNumbers.Clean(values.Name);
        if (patch.Has("name"))
        {
            ValidateName(errors, name);
        }

        if (patch.Has("status") && (values.Status is null || !PriceListStatuses.All.Contains(values.Status)))
        {
            errors["status"] = [$"Must be one of: {string.Join(", ", PriceListStatuses.All)}."];
        }

        if (patch.Has("isDefault") && values.IsDefault != true)
        {
            errors["isDefault"] = ["Only true is accepted: make another list the default instead."];
        }

        if (patch.Has("pricesIncludeTax") && values.PricesIncludeTax is null)
        {
            errors["pricesIncludeTax"] = ["Must be true or false."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var list = await FindVisibleAsync(id, tracked: true, cancellationToken);
        if (list is null)
        {
            return AppError.NotFound();
        }

        if (list.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var validFrom = patch.Has("validFrom") ? values.ValidFrom : list.ValidFrom;
        var validTo = patch.Has("validTo") ? values.ValidTo : list.ValidTo;
        var baseId = patch.Has("basePriceListId") ? values.BasePriceListId : list.BasePriceListId;
        var discount = patch.Has("discountPercent") ? values.DiscountPercent : list.DiscountPercent;
        var pricesIncludeTax = values.PricesIncludeTax ?? list.PricesIncludeTax;
        var status = values.Status ?? list.Status;
        ValidateValidity(errors, validFrom, validTo);
        ValidateRule(errors, baseId, discount);
        if (baseId is not null && list.Kind != PriceListKinds.Sales)
        {
            errors["basePriceListId"] = ["Only sales lists can derive from a base list."];
        }

        if (values.IsDefault == true && list.Kind != PriceListKinds.Sales)
        {
            errors["isDefault"] = ["Only sales lists can be the default."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var makeDefault = values.IsDefault == true && !list.IsDefault;
        if (status != PriceListStatuses.Active && (list.IsDefault || makeDefault))
        {
            return AppError.Conflict("pricing.default_inactive", "The default price list must stay active");
        }

        var derived = await db.PriceLists.AsNoTracking()
            .Where(l => l.BasePriceListId == list.Id && l.Status != PriceListStatuses.Archived)
            .Select(l => new { l.PricesIncludeTax })
            .ToListAsync(cancellationToken);
        if (derived.Count > 0 && baseId is not null)
        {
            return AppError.Conflict("pricing.base_chain", "A list used as a base cannot derive from another list");
        }

        if (derived.Count > 0 && status == PriceListStatuses.Archived)
        {
            return AppError.Conflict("pricing.list_in_use", "Archive the lists derived from this one first");
        }

        if (derived.Any(d => d.PricesIncludeTax != pricesIncludeTax))
        {
            return AppError.Conflict("pricing.tax_mode_mismatch", "Derived lists must use the same VAT mode as their base");
        }

        if (baseId is { } newBase && await CheckBaseAsync(list.Id, newBase, list.Currency, pricesIncludeTax, cancellationToken) is { } baseError)
        {
            return baseError;
        }

        var now = Now();
        if (makeDefault)
        {
            var current = await db.PriceLists
                .SingleOrDefaultAsync(l => l.IsDefault && l.Kind == PriceListKinds.Sales && l.Currency == list.Currency, cancellationToken);
            if (current is not null && await ClearDefaultAsync(current, now, cancellationToken) is { } clearError)
            {
                return clearError;
            }

            list.IsDefault = true;
        }

        list.Name = name ?? list.Name;
        list.PricesIncludeTax = pricesIncludeTax;
        list.ValidFrom = validFrom;
        list.ValidTo = validTo;
        list.BasePriceListId = baseId;
        list.DiscountPercent = discount;
        list.SetStatus(status, now);
        list.Touch(UserId, now);
        return await SaveAsync(() => ToResponse(list), cancellationToken);
    }

    // ---- Items ----

    public async Task<AppResult<PriceListItemPageResponse>> ListItemsAsync(
        Guid priceListId, Guid? variantId, int limit, Guid? after, CancellationToken cancellationToken)
    {
        if (await FindVisibleAsync(priceListId, tracked: false, cancellationToken) is null)
        {
            return AppError.NotFound();
        }

        var query = db.Items.AsNoTracking().Where(i => i.PriceListId == priceListId);
        if (variantId is { } variant)
        {
            query = query.Where(i => i.VariantId == variant);
        }

        if (after is { } afterId)
        {
            query = query.Where(i => EF.Functions.GreaterThan(ValueTuple.Create(i.PriceListId, i.Id), ValueTuple.Create(priceListId, afterId)));
        }

        var page = await query.OrderBy(i => i.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new PriceListItemPageResponse(
            page.Select(ToResponse).ToList(),
            new PricingPageInfo(hasMore ? IdCursor.Encode(page[^1].Id) : null, hasMore, limit));
    }

    public async Task<AppResult<UpsertPriceListItemsResponse>> UpsertItemsAsync(
        Guid priceListId, UpsertPriceListItemsRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var inputs = request.Items ?? [];
        if (inputs.Count is 0 or > MaxItemsPerUpsert)
        {
            errors["items"] = [$"Must contain 1–{MaxItemsPerUpsert} items."];
        }

        var keys = new HashSet<(Guid, decimal)>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input.VariantId is null || input.VariantId == Guid.Empty)
            {
                errors[$"items[{i}].variantId"] = ["Required."];
            }

            var minQuantity = input.MinQuantity ?? 1m;
            if (!PricingNumbers.IsQuantity(minQuantity))
            {
                errors[$"items[{i}].minQuantity"] = ["Must be greater than 0 with at most 3 decimals."];
            }

            if (input.UnitPrice is not { } price || !PricingNumbers.IsMoney(price))
            {
                errors[$"items[{i}].unitPrice"] = ["Required: 0 or more with at most 4 decimals."];
            }

            if (input.VariantId is { } variantId && !keys.Add((variantId, minQuantity)))
            {
                errors[$"items[{i}]"] = ["Duplicate variantId + minQuantity in this request."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (await FindVisibleAsync(priceListId, tracked: false, cancellationToken) is not { } list)
        {
            return AppError.NotFound();
        }

        if (list.Status == PriceListStatuses.Archived)
        {
            return AppError.Conflict("pricing.list_archived", "Archived price lists cannot change");
        }

        var variantIds = inputs.Select(i => i.VariantId!.Value).Distinct().ToList();
        var known = await variants.FindVariantsAsync(variantIds, cancellationToken);
        for (var i = 0; i < inputs.Count; i++)
        {
            if (!known.ContainsKey(inputs[i].VariantId!.Value))
            {
                errors[$"items[{i}].variantId"] = ["Variant not found."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var existing = (await db.Items
                .Where(i => i.PriceListId == priceListId && variantIds.Contains(i.VariantId))
                .ToListAsync(cancellationToken))
            .ToDictionary(i => (i.VariantId, i.MinQuantity));
        var now = Now();
        var (created, updated) = (0, 0);
        var results = new List<PriceListItem>(inputs.Count);
        foreach (var input in inputs)
        {
            var key = (input.VariantId!.Value, input.MinQuantity ?? 1m);
            if (existing.TryGetValue(key, out var item))
            {
                if (item.UnitPrice != input.UnitPrice!.Value)
                {
                    item.UnitPrice = input.UnitPrice.Value;
                    item.Touch(UserId, now);
                    updated++;
                }
            }
            else
            {
                item = PriceListItem.Create(TenantId, priceListId, key.Item1, key.Item2, input.UnitPrice!.Value, UserId, now);
                db.Items.Add(item);
                created++;
            }

            results.Add(item);
        }

        return await SaveAsync(() => new UpsertPriceListItemsResponse(created, updated, results.Select(ToResponse).ToList()), cancellationToken);
    }

    public async Task<AppResult<bool>> DeleteItemAsync(Guid priceListId, Guid itemId, CancellationToken cancellationToken)
    {
        if (await FindVisibleAsync(priceListId, tracked: false, cancellationToken) is not { } list)
        {
            return AppError.NotFound();
        }

        if (list.Status == PriceListStatuses.Archived)
        {
            return AppError.Conflict("pricing.list_archived", "Archived price lists cannot change");
        }

        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId && i.PriceListId == priceListId, cancellationToken);
        if (item is null)
        {
            return AppError.NotFound();
        }

        db.Items.Remove(item);
        return await SaveAsync(() => true, cancellationToken);
    }

    // ---- Helpers ----

    private async Task<PriceList?> FindVisibleAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var canReadCost = CanReadCost;
        var source = tracked ? db.PriceLists : db.PriceLists.AsNoTracking();
        return await source.SingleOrDefaultAsync(l => l.Id == id && (canReadCost || l.Kind == PriceListKinds.Sales), cancellationToken);
    }

    /// <summary>The base must be another non-archived, non-derived sales list in the same currency and VAT mode.</summary>
    private async Task<AppError?> CheckBaseAsync(Guid? listId, Guid baseId, string currency, bool pricesIncludeTax, CancellationToken cancellationToken)
    {
        var baseList = await db.PriceLists.AsNoTracking().SingleOrDefaultAsync(l => l.Id == baseId, cancellationToken);
        if (baseId == listId || baseList is null || baseList.Kind != PriceListKinds.Sales || baseList.Status == PriceListStatuses.Archived)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["basePriceListId"] = ["Must be another sales list that is not archived."] });
        }

        if (baseList.BasePriceListId is not null)
        {
            return AppError.Conflict("pricing.base_chain", "The base list derives from another list; only one level is allowed");
        }

        if (baseList.Currency != currency)
        {
            return AppError.Conflict("pricing.currency_mismatch", "The base list uses another currency");
        }

        return baseList.PricesIncludeTax != pricesIncludeTax
            ? AppError.Conflict("pricing.tax_mode_mismatch", "The base list uses another VAT mode")
            : null;
    }

    /// <summary>Clears the current default first: the partial unique index allows one default per currency at a time.</summary>
    private async Task<AppError?> ClearDefaultAsync(PriceList current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        current.IsDefault = false;
        current.Touch(UserId, now);
        return (await SaveAsync(() => true, cancellationToken)).Error;
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
                PricingDbContext.NameUniqueIndex => AppError.Conflict("pricing.name_taken", "A price list with this name already exists"),
                PricingDbContext.DefaultUniqueIndex => AppError.Conflict("pricing.default_conflict", "Another default list was set concurrently; retry"),
                PricingDbContext.ItemUniqueIndex => AppError.Conflict("pricing.item_conflict", "The same item was written concurrently; retry"),
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

    private static void ValidateValidity(Dictionary<string, string[]> errors, DateOnly? validFrom, DateOnly? validTo)
    {
        if (validFrom is { } from && validTo is { } to && to < from)
        {
            errors["validTo"] = ["Must not be before validFrom."];
        }
    }

    private static void ValidateRule(Dictionary<string, string[]> errors, Guid? baseId, decimal? discount)
    {
        if ((baseId is null) != (discount is null))
        {
            errors["discountPercent"] = ["basePriceListId and discountPercent are set together."];
        }
        else if (discount is { } percent && !PricingNumbers.IsPercent(percent))
        {
            errors["discountPercent"] = ["Must be between 0 and 100 with at most 2 decimals."];
        }
    }

    /// <summary>Microsecond precision: what PostgreSQL <c>timestamptz</c> stores.</summary>
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    private static PriceListResponse ToResponse(PriceList l) => new(
        l.Id, l.Name, l.Kind, l.Currency, l.IsDefault, l.PricesIncludeTax, l.ValidFrom, l.ValidTo, l.BasePriceListId, l.DiscountPercent,
        l.Status, l.RowVersion, l.CreatedAt, l.UpdatedAt);

    private static PriceListItemResponse ToResponse(PriceListItem i) =>
        new(i.Id, i.PriceListId, i.VariantId, i.MinQuantity, i.UnitPrice, i.RowVersion, i.CreatedAt, i.UpdatedAt);
}
