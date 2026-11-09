using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Domain;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Pricing.Application;

/// <summary>
/// <see cref="IPriceResolver"/> over the pricing schema. Read-only; a fixed number of queries per quote (customer
/// profile, variants, candidate lists, base lists, items) regardless of the line count.
/// </summary>
internal sealed class PriceResolver(
    PricingDbContext db,
    ICustomerPricingDirectory customers,
    IPricingVariantDirectory variants,
    ITenantCurrencyProvider tenantCurrency) : IPriceResolver
{
    public const int MaxLines = 500;

    public async Task<PricingResult<PriceQuote>> QuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var requestedCurrency = PricingNumbers.NormalizeCurrency(request.Currency, out var currencyValid);
        if (!currencyValid)
        {
            errors["currency"] = ["Must be a 3-letter ISO 4217 code."];
        }

        var requestLines = request.Lines ?? [];
        if (requestLines.Count is 0 or > MaxLines)
        {
            errors["lines"] = [$"Must contain 1–{MaxLines} lines."];
        }
        else
        {
            for (var i = 0; i < requestLines.Count; i++)
            {
                if (!PricingNumbers.IsQuantity(requestLines[i].Quantity))
                {
                    errors[$"lines[{i}].quantity"] = ["Must be greater than 0 with at most 3 decimals."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return PricingError.Validation(errors);
        }

        CustomerPricingProfile? profile = null;
        if (request.CustomerId is { } customerId)
        {
            profile = await customers.FindAsync(customerId, cancellationToken);
            if (profile is null)
            {
                errors["customerId"] = ["Customer not found."];
            }
        }

        var variantIds = requestLines.Select(l => l.VariantId).Distinct().ToList();
        var known = await variants.FindVariantsAsync(variantIds, cancellationToken);
        for (var i = 0; i < requestLines.Count; i++)
        {
            if (!known.ContainsKey(requestLines[i].VariantId))
            {
                errors[$"lines[{i}].variantId"] = ["Variant not found."];
            }
        }

        if (errors.Count > 0)
        {
            return PricingError.Validation(errors);
        }

        var currency = requestedCurrency ?? profile?.Currency ?? await tenantCurrency.GetDefaultCurrencyAsync(cancellationToken);

        // Candidate lists in resolution order: customer → dealer group → tenant default (domain-model §4).
        var assigned = new List<(string Source, Guid Id)>();
        if (profile?.PriceListId is { } customerList)
        {
            assigned.Add((PriceSources.CustomerPriceList, customerList));
        }

        if (profile?.DealerGroupPriceListId is { } groupList)
        {
            assigned.Add((PriceSources.DealerGroupPriceList, groupList));
        }

        var assignedIds = assigned.Select(a => a.Id).ToList();
        var loaded = await db.PriceLists.AsNoTracking()
            .Where(l => assignedIds.Contains(l.Id) || (l.IsDefault && l.Kind == PriceListKinds.Sales && l.Currency == currency))
            .ToListAsync(cancellationToken);
        var baseIds = loaded.Where(l => l.BasePriceListId is not null).Select(l => l.BasePriceListId!.Value)
            .Except(loaded.Select(l => l.Id)).ToList();
        if (baseIds.Count > 0)
        {
            loaded.AddRange(await db.PriceLists.AsNoTracking().Where(l => baseIds.Contains(l.Id)).ToListAsync(cancellationToken));
        }

        var lists = loaded.ToDictionary(l => l.Id);
        var candidates = new List<PriceCandidate>();
        foreach (var (source, id) in assigned)
        {
            if (lists.TryGetValue(id, out var list) && candidates.All(c => c.List.Id != id))
            {
                candidates.Add(new PriceCandidate(source, list));
            }
        }

        if (loaded.FirstOrDefault(l => l.IsDefault && l.Kind == PriceListKinds.Sales && l.Currency == currency) is { } defaultList
            && candidates.All(c => c.List.Id != defaultList.Id))
        {
            candidates.Add(new PriceCandidate(PriceSources.DefaultPriceList, defaultList));
        }

        var listIds = lists.Keys.ToList();
        var items = (await db.Items.AsNoTracking()
                .Where(i => listIds.Contains(i.PriceListId) && variantIds.Contains(i.VariantId))
                .ToListAsync(cancellationToken))
            .ToLookup(i => (i.PriceListId, i.VariantId));

        var lines = requestLines
            .Select(line => PriceResolution.Resolve(known[line.VariantId], line.Quantity, currency, request.Date, candidates, lists, items))
            .ToList();
        return new PriceQuote(request.CustomerId, currency, request.Date, profile?.DealerGroupDiscountPercent, lines);
    }
}

/// <summary><see cref="IPriceListDirectory"/> for other modules; purchase lists included (callers check the kind).</summary>
internal sealed class PriceListDirectory(PricingDbContext db) : IPriceListDirectory
{
    public async Task<PriceListInfo?> FindAsync(Guid priceListId, CancellationToken cancellationToken) =>
        await db.PriceLists.AsNoTracking()
            .Where(l => l.Id == priceListId)
            .Select(l => new PriceListInfo(l.Id, l.Name, l.Kind, l.Currency, l.Status))
            .SingleOrDefaultAsync(cancellationToken);
}
