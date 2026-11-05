namespace BayiOS.Modules.Orders.Contracts;

// ---- Requests ----

/// <param name="WarehouseId">Null = the tenant's default warehouse.</param>
/// <param name="Source">manual (default), whatsapp_ai, import, mobile.</param>
/// <param name="Currency">ISO 4217; null = the customer's currency, else the tenant default.</param>
/// <param name="PricesIncludeTax">Null = the tax mode of the first resolved list price, else false.</param>
/// <param name="ShippingAddressId">A shipping address of the customer; null = the customer's default shipping address at submit.</param>
/// <param name="BillingAddressId">A billing address of the customer; null = the customer's default billing address at submit.</param>
public sealed record CreateOrderRequest(
    Guid? CustomerId,
    Guid? WarehouseId,
    string? Source,
    Guid? SourceReferenceId,
    string? Currency,
    bool? PricesIncludeTax,
    DateOnly? RequestedDeliveryDate,
    string? Notes,
    IReadOnlyList<OrderLineInput>? Lines,
    Guid? ShippingAddressId = null,
    Guid? BillingAddressId = null);

/// <summary>
/// One complete line. <paramref name="UnitPrice"/> null = the resolved list price; a value is a manual override
/// (<c>orders.price.override</c>). Discount: percent or amount (not both); none given = the dealer group default.
/// </summary>
/// <param name="Id">An existing line of this order to keep (edits); null = new line.</param>
public sealed record OrderLineInput(
    Guid? Id,
    Guid? VariantId,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? DiscountPercent,
    decimal? DiscountAmount);

/// <summary>
/// Merge patch of a draft. <c>lines</c>, when present, replaces all lines; prices are resolved again.
/// <c>shippingAddressId</c> / <c>billingAddressId</c>: an address of the (new) customer, or null = the customer's
/// default; changing the customer without them resets both to the default.
/// </summary>
public sealed record UpdateOrderRequest(
    Guid? CustomerId,
    Guid? WarehouseId,
    bool? PricesIncludeTax,
    DateOnly? RequestedDeliveryDate,
    string? Notes,
    IReadOnlyList<OrderLineInput>? Lines,
    Guid? ShippingAddressId = null,
    Guid? BillingAddressId = null);

/// <param name="ConfirmNegativeStock">Explicit confirmation under the <c>warn</c> negative-stock policy.</param>
public sealed record ApproveOrderRequest(bool? ConfirmNegativeStock);

/// <summary>Optional body of <c>submit</c>.</summary>
/// <param name="ConfirmNegativeStock">
/// Only used when the submit approves at once (tenant <c>requireOrderApproval = false</c>): the <c>warn</c>
/// negative-stock confirmation, as on approve.
/// </param>
public sealed record SubmitOrderRequest(bool? ConfirmNegativeStock);

/// <summary>Reason is required (PRD ORD-07 for cancel; reject and short-close likewise).</summary>
public sealed record OrderReasonRequest(string? Reason);

// ---- Responses ----

public sealed record TaxBreakdownResponse(decimal Rate, decimal TaxBase, decimal Tax, decimal Total);

/// <param name="PricesIncludeTax">Whether unit prices, subtotal and discount include VAT.</param>
/// <param name="NetTotal">Total excluding VAT.</param>
/// <param name="HasUnpricedLines">True while any line has no price (such lines are not in the totals).</param>
public sealed record OrderTotalsResponse(
    bool PricesIncludeTax,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal NetTotal,
    decimal GrandTotal,
    IReadOnlyList<TaxBreakdownResponse> TaxBreakdown,
    bool HasUnpricedLines);

/// <param name="Totals">Null when the caller may not see prices (warehouse_staff, roles-permissions footnote 8).</param>
/// <param name="CustomerName">Current customer name; null when the caller lacks <c>customers.read</c> (warehouse_staff).</param>
/// <param name="WarehouseName">Current warehouse name.</param>
/// <param name="CreatedByName">Display name of the creator (full name, else e-mail local part).</param>
/// <param name="ApprovedByName">Display name of the approver.</param>
/// <param name="ShippingAddressId">The chosen shipping address (null = the customer's default).</param>
/// <param name="BillingAddressId">The chosen billing address (null = the customer's default).</param>
/// <param name="ShippingAddress">
/// Draft: a live preview of the chosen (or default) address. From submit on: the snapshot taken at submit (or at
/// approval if the order had none), never changed after approval. Null when the customer has no such address, and
/// for callers without <c>customers.read</c> (warehouse_staff, footnote 8).
/// </param>
/// <param name="BillingAddress">As <paramref name="ShippingAddress"/>, for the billing address.</param>
/// <param name="AddressesAreSnapshot">True once the addresses are the order's own snapshot (not a draft preview).</param>
public sealed record OrderResponse(
    Guid Id,
    string Number,
    Guid CustomerId,
    string? CustomerName,
    Guid WarehouseId,
    string? WarehouseName,
    string Status,
    string FulfillmentStatus,
    string Source,
    Guid? SourceReferenceId,
    string Currency,
    DateOnly PriceDate,
    DateOnly? RequestedDeliveryDate,
    string? Notes,
    OrderTotalsResponse? Totals,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    string? ApprovedByName,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    bool HasShortage,
    long Version,
    Guid? ShippingAddressId,
    Guid? BillingAddressId,
    OrderAddressResponse? ShippingAddress,
    OrderAddressResponse? BillingAddress,
    bool AddressesAreSnapshot);

/// <summary>An order address (customer address copied onto the order).</summary>
/// <param name="AddressId">The customer address it was copied from (it may since have changed or been removed).</param>
/// <param name="Country">ISO 3166-1 alpha-2.</param>
public sealed record OrderAddressResponse(
    Guid AddressId,
    string? Title,
    string Line1,
    string? Line2,
    string? District,
    string City,
    string? PostalCode,
    string Country,
    string? ContactName,
    string? ContactPhone);

/// <param name="PriceStatus"><c>priced</c> or <c>no_price</c>.</param>
/// <param name="OpenQuantity">quantity − shipped − cancelled.</param>
public sealed record OrderLineResponse(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string Sku,
    string Name,
    string Unit,
    decimal Quantity,
    string PriceStatus,
    decimal? UnitPrice,
    string? PriceSource,
    Guid? PriceListId,
    bool PriceOverridden,
    decimal? DiscountPercent,
    decimal? DiscountAmount,
    decimal TaxRate,
    decimal? LineSubtotal,
    decimal? LineDiscount,
    decimal? LineTotal,
    decimal ReservedQuantity,
    decimal ShippedQuantity,
    decimal CancelledQuantity,
    decimal OpenQuantity);

/// <param name="ChangedByName">Display name of <paramref name="ChangedBy"/> (full name, else e-mail local part).</param>
public sealed record OrderHistoryResponse(
    Guid Id,
    string Action,
    string? FromStatus,
    string ToStatus,
    string FulfillmentStatus,
    string? Reason,
    Guid? ChangedBy,
    string? ChangedByName,
    DateTimeOffset ChangedAt);

public sealed record OrderShipmentLineResponse(Guid Id, Guid OrderLineId, Guid VariantId, Guid? LocationId, decimal Quantity);

public sealed record OrderShipmentResponse(
    Guid Id,
    string? Carrier,
    string? TrackingNumber,
    int? PackageCount,
    DateTimeOffset ShippedAt,
    Guid? ShippedBy,
    string? ShippedByName,
    IReadOnlyList<OrderShipmentLineResponse> Lines);

public sealed record OrderDetailResponse(
    OrderResponse Order,
    IReadOnlyList<OrderLineResponse> Lines,
    IReadOnlyList<OrderHistoryResponse> History,
    IReadOnlyList<OrderShipmentResponse> Shipments);

/// <param name="GrandTotal">Null when the caller may not see prices (warehouse_staff, roles-permissions footnote 8).</param>
/// <param name="CustomerName">Current customer name; null when the caller lacks <c>customers.read</c> (warehouse_staff).</param>
/// <param name="WarehouseName">Current warehouse name.</param>
public sealed record OrderSummaryResponse(
    Guid Id,
    string Number,
    Guid CustomerId,
    string? CustomerName,
    Guid WarehouseId,
    string? WarehouseName,
    string Status,
    string FulfillmentStatus,
    string Source,
    string Currency,
    decimal? GrandTotal,
    int LineCount,
    DateTimeOffset CreatedAt,
    long Version);

public sealed record OrderPageInfo(string? NextCursor, bool HasMore, int Limit);

public sealed record OrderPageResponse(IReadOnlyList<OrderSummaryResponse> Items, OrderPageInfo Page);
