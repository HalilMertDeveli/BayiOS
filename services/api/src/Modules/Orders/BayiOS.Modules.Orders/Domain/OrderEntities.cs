using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Orders.Domain;

/// <summary>Main order status (inventory-and-orders §3, data-model §5.8). Partial shipment is <see cref="FulfillmentStatuses"/>.</summary>
internal static class OrderStatuses
{
    public const string Draft = "draft";
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Picking = "picking";
    public const string Packed = "packed";
    public const string Shipped = "shipped";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Draft, PendingApproval, Approved, Picking, Packed, Shipped, Completed, Cancelled];
}

/// <summary>Derived shipment progress (§3.1: no "PartiallyShipped" status in the main machine).</summary>
internal static class FulfillmentStatuses
{
    public const string None = "none";
    public const string Partial = "partial";
    public const string Full = "full";

    public static readonly IReadOnlyList<string> All = [None, Partial, Full];
}

/// <summary>Where the order came from (PRD ORD-01, data-model §5.8). AI drafts still need a human approval (ADR 0010).</summary>
internal static class OrderSources
{
    public const string Manual = "manual";
    public const string WhatsappAi = "whatsapp_ai";
    public const string Import = "import";
    public const string Mobile = "mobile";

    public static readonly IReadOnlyList<string> All = [Manual, WhatsappAi, Import, Mobile];
}

/// <summary>How a line got its unit price: a Pricing source (<c>PriceSources</c>) or a manual override.</summary>
internal static class LinePriceSources
{
    public const string Manual = "manual";
}

/// <summary>Order lifecycle actions; every one is a row in <c>order_status_history</c>.</summary>
internal static class OrderActions
{
    public const string Create = "create";
    public const string Submit = "submit";
    public const string Withdraw = "withdraw";
    public const string Reject = "reject";
    public const string Approve = "approve";
    public const string StartPicking = "start_picking";
    public const string Pack = "pack";
    public const string Ship = "ship";
    public const string CloseRemaining = "close_remaining";
    public const string Complete = "complete";
    public const string Cancel = "cancel";

    /// <summary>Warehouse reported a picking/packing shortage: a timeline entry, the status does not change.</summary>
    public const string ReportShortage = "report_shortage";

    public static readonly IReadOnlyList<string> All =
        [Create, Submit, Withdraw, Reject, Approve, StartPicking, Pack, Ship, CloseRemaining, Complete, Cancel, ReportShortage];

    /// <summary>The state-machine actions (everything except <see cref="Create"/>).</summary>
    public static readonly IReadOnlyList<string> Transitions = [.. All.Where(a => a != Create)];
}

/// <summary>Customer address types (customers.customer_addresses.type).</summary>
internal static class OrderAddressTypes
{
    public const string Shipping = "shipping";
    public const string Billing = "billing";
}

/// <summary>A customer address copied onto the order (stored as jsonb; the source address may later change or go).</summary>
internal sealed record OrderAddressSnapshot(
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

/// <summary>
/// orders.orders (data-model §5.8): the order header. Totals are derived from the lines on the server only
/// (<see cref="OrderMath"/>); amounts are numeric(18,4) holding values rounded to 2 decimals (PRD ORD-04).
/// </summary>
internal sealed class Order : ITenantOwned
{
    private Order()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    /// <summary>Tenant-unique, gapless per prefix and year: <c>{prefix}-2026-000123</c>, prefix from the tenant setting (PRD ORD-14).</summary>
    public string Number { get; private init; } = string.Empty;

    /// <summary>→ customers.customers (no cross-module FK).</summary>
    public Guid CustomerId { get; set; }

    /// <summary>→ warehouse.warehouses (MVP: one warehouse per order).</summary>
    public Guid WarehouseId { get; set; }

    public string Status { get; set; } = OrderStatuses.Draft;

    public string FulfillmentStatus { get; set; } = FulfillmentStatuses.None;

    public string Source { get; private init; } = OrderSources.Manual;

    /// <summary>Upstream record (e.g. the WhatsApp parse request or import job), when the source has one.</summary>
    public Guid? SourceReferenceId { get; private init; }

    public string Currency { get; private init; } = "TRY";

    /// <summary>Whether unit prices and line totals of this order include VAT.</summary>
    public bool PricesIncludeTax { get; set; }

    /// <summary>Date prices were resolved for (order date, Europe/Istanbul).</summary>
    public DateOnly PriceDate { get; set; }

    public DateOnly? RequestedDeliveryDate { get; set; }

    public string? Notes { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountTotal { get; set; }

    public decimal TaxTotal { get; set; }

    public decimal GrandTotal { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>Warehouse reported a picking or packing shortage (WHT-06). Never cleared: it is part of the history.</summary>
    public bool HasShortage { get; set; }

    /// <summary>Chosen shipping address of the customer (draft choice; null = the customer's default at submit).</summary>
    public Guid? ShippingAddressId { get; set; }

    /// <summary>Chosen billing address of the customer (draft choice; null = the customer's default at submit).</summary>
    public Guid? BillingAddressId { get; set; }

    /// <summary>
    /// Snapshot (jsonb) taken at submit — or at approval when missing — and never changed after approval (database
    /// trigger <c>trg_orders_address_snapshot_immutable</c>).
    /// </summary>
    public OrderAddressSnapshot? ShippingAddress { get; set; }

    /// <summary>As <see cref="ShippingAddress"/>, for billing.</summary>
    public OrderAddressSnapshot? BillingAddress { get; set; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid? CreatedBy { get; private init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public List<OrderLine> Lines { get; private init; } = [];

    public void Touch(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    /// <summary>Recomputes the stored totals from the priced lines (unpriced lines contribute nothing).</summary>
    public void Recalculate()
    {
        var totals = OrderMath.Totals(Lines, PricesIncludeTax);
        Subtotal = totals.Subtotal;
        DiscountTotal = totals.DiscountTotal;
        TaxTotal = totals.TaxTotal;
        GrandTotal = totals.GrandTotal;
    }

    public static Order Create(
        Guid tenantId,
        string number,
        Guid customerId,
        Guid warehouseId,
        string source,
        Guid? sourceReferenceId,
        string currency,
        DateOnly priceDate,
        Guid userId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Number = number,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Source = source,
            SourceReferenceId = sourceReferenceId,
            Currency = currency,
            PriceDate = priceDate,
            CreatedAt = now,
            CreatedBy = userId,
        };
}

/// <summary>
/// orders.order_lines: product snapshot, quantity, price snapshot and the fulfilment counters. Invariants (DB CHECKs):
/// <c>qty_reserved + qty_shipped + qty_cancelled ≤ quantity</c> (I7), at most one discount type per line (ORD-03).
/// </summary>
internal sealed class OrderLine : ITenantOwned, IPricedLine
{
    private OrderLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid OrderId { get; private init; }

    public int LineNo { get; set; }

    /// <summary>→ catalog.product_variants (no cross-module FK).</summary>
    public Guid VariantId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    /// <summary>In the variant's base unit; numeric(18,3).</summary>
    public decimal Quantity { get; set; }

    /// <summary>numeric(18,4) in the order's tax mode; null = no price could be resolved (never treated as 0).</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>Pricing source (customer / dealer group / default list) or <c>manual</c>.</summary>
    public string? PriceSource { get; set; }

    /// <summary>→ pricing.price_lists, the list whose rule produced the price.</summary>
    public Guid? PriceListId { get; set; }

    public bool PriceOverridden { get; set; }

    public decimal? DiscountPercent { get; set; }

    public decimal? DiscountAmount { get; set; }

    /// <summary>VAT % of the variant's product at pricing time.</summary>
    public decimal TaxRate { get; set; }

    /// <summary>round2(quantity × unit price); null while unpriced.</summary>
    public decimal? LineSubtotal { get; set; }

    public decimal? LineDiscount { get; set; }

    /// <summary>LineSubtotal − LineDiscount, in the order's tax mode.</summary>
    public decimal? LineTotal { get; set; }

    /// <summary>Open reserved quantity (mirror of the line's active inventory reservations).</summary>
    public decimal QtyReserved { get; set; }

    public decimal QtyShipped { get; set; }

    /// <summary>Closed without shipping (short-close).</summary>
    public decimal QtyCancelled { get; set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid? CreatedBy { get; private init; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public decimal Remaining => Quantity - QtyShipped - QtyCancelled;

    public static OrderLine Create(Guid tenantId, Guid orderId, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        OrderId = orderId,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>orders.order_status_history: append-only timeline (PRD ORD-12). The runtime role may only INSERT/SELECT.</summary>
internal sealed class OrderStatusChange : ITenantOwned
{
    private OrderStatusChange()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid OrderId { get; private init; }

    public string Action { get; private init; } = string.Empty;

    public string? FromStatus { get; private init; }

    public string ToStatus { get; private init; } = string.Empty;

    public string FulfillmentStatus { get; private init; } = FulfillmentStatuses.None;

    public string? Reason { get; private init; }

    public Guid? ChangedBy { get; private init; }

    public DateTimeOffset ChangedAt { get; private init; }

    public string? CorrelationId { get; private init; }

    public static OrderStatusChange Create(
        Order order, string action, string? fromStatus, string? reason, Guid userId, DateTimeOffset now, string? correlationId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = order.TenantId,
            OrderId = order.Id,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = order.Status,
            FulfillmentStatus = order.FulfillmentStatus,
            Reason = reason,
            ChangedBy = userId,
            ChangedAt = now,
            CorrelationId = correlationId,
        };
}

/// <summary>
/// orders.order_shipments: the order-side record of a confirmed shipment, written only through
/// <c>IOrderFulfillment.ShipAsync</c>. Its id is the Warehouse shipment id (<c>warehouse.shipments</c>, the
/// operational document) and the <c>source_id</c> of the <c>shipment_out</c> movements; line ids are the Warehouse
/// shipment line ids (<c>source_line_id</c>).
/// </summary>
internal sealed class OrderShipment : ITenantOwned
{
    private OrderShipment()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid OrderId { get; private init; }

    public string? Carrier { get; private init; }

    public string? TrackingNumber { get; private init; }

    public int? PackageCount { get; private init; }

    public DateTimeOffset ShippedAt { get; private init; }

    public Guid? ShippedBy { get; private init; }

    public string? CorrelationId { get; private init; }

    public List<OrderShipmentLine> Lines { get; private init; } = [];

    public static OrderShipment Create(
        Guid id, Order order, string? carrier, string? trackingNumber, int? packageCount, Guid userId, DateTimeOffset now, string? correlationId) => new()
        {
            Id = id,
            TenantId = order.TenantId,
            OrderId = order.Id,
            Carrier = carrier,
            TrackingNumber = trackingNumber,
            PackageCount = packageCount,
            ShippedAt = now,
            ShippedBy = userId,
            CorrelationId = correlationId,
        };
}

/// <summary>orders.order_shipment_lines: one per (order line, location); id = the movement's <c>source_line_id</c>.</summary>
internal sealed class OrderShipmentLine : ITenantOwned
{
    private OrderShipmentLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid ShipmentId { get; private init; }

    public Guid OrderLineId { get; private init; }

    public Guid VariantId { get; private init; }

    public Guid? LocationId { get; private init; }

    public decimal Quantity { get; private init; }

    public static OrderShipmentLine Create(Guid id, OrderShipment shipment, OrderLine line, Guid? locationId, decimal quantity) => new()
    {
        Id = id,
        TenantId = shipment.TenantId,
        ShipmentId = shipment.Id,
        OrderLineId = line.Id,
        VariantId = line.VariantId,
        LocationId = locationId,
        Quantity = quantity,
    };
}

/// <summary>
/// orders.order_number_sequences: one counter row per tenant, number prefix and year (row lock → gapless numbers).
/// A prefix change starts (or resumes) that prefix's own counter; numbers stay unique because the prefix is part of them.
/// </summary>
internal sealed class OrderNumberSequence : ITenantOwned
{
    private OrderNumberSequence()
    {
    }

    public Guid TenantId { get; private init; }

    public string Prefix { get; private init; } = "SIP";

    public int Year { get; private init; }

    public long NextValue { get; private init; }
}
