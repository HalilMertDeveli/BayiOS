using BayiOS.Modules.Integrations.Matching;

namespace BayiOS.Modules.Integrations.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Ports. Integrations references no other module: the host implements these over Catalog (read-only, tenant query
// filter + RLS) and Orders (the draft-create command), on the request UnitOfWork connection and transaction.
// ------------------------------------------------------------------------------------------------------------------

/// <param name="Truncated">The tenant has more active variants than the limit; only exact codes see the rest.</param>
public sealed record AssistantCatalogSnapshot(IReadOnlyList<CatalogEntry> Entries, bool Truncated);

/// <summary>The request tenant's catalog as the matcher needs it: active variants only, no price, no stock.</summary>
public interface IAssistantCatalog
{
    /// <summary>Active variants of active products (up to <paramref name="limit"/>, by product name then SKU).</summary>
    Task<AssistantCatalogSnapshot> LoadActiveVariantsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Active variants whose SKU or barcode equals one of the (normalised, upper-case) codes.</summary>
    Task<IReadOnlyList<CatalogEntry>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Active variants (of active products) among the given ids; ids of other tenants or inactive variants are absent.</summary>
    Task<IReadOnlyList<CatalogEntry>> FindByIdsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

public sealed record AssistantDraftLine(Guid VariantId, decimal Quantity);

/// <summary>A draft order with source <c>whatsapp_ai</c>; prices are resolved by Pricing (never supplied here).</summary>
/// <param name="ParseId">Stored as the order's source reference.</param>
public sealed record AssistantDraftOrder(Guid CustomerId, Guid? WarehouseId, Guid ParseId, string? Notes, IReadOnlyList<AssistantDraftLine> Lines);

/// <summary>Either the created order or the Orders problem (status, code, title, field errors).</summary>
public sealed record AssistantDraftResult(
    Guid? OrderId,
    string? OrderNumber,
    int Status = 0,
    string? Code = null,
    string? Title = null,
    IDictionary<string, string[]>? Errors = null)
{
    public bool Succeeded => OrderId is not null;
}

/// <summary>Creates the draft through the Orders module's own create command (validation, pricing, audit, history).</summary>
public interface IAssistantOrderDrafts
{
    Task<AssistantDraftResult> CreateDraftAsync(AssistantDraftOrder draft, string? correlationId, CancellationToken cancellationToken);
}
