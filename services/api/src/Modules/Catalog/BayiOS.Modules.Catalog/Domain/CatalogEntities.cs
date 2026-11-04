using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Catalog.Domain;

/// <summary>Product / variant lifecycle (PRD CAT-02). Nothing in the catalog is physically deleted.</summary>
internal static class CatalogStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Active, Inactive, Archived];
}

/// <summary>Shared columns of every catalog table (data-model §1): tenant, audit stamps, optimistic concurrency.</summary>
internal abstract class CatalogEntity : ITenantOwned
{
    public Guid Id { get; protected init; }

    public Guid TenantId { get; protected init; }

    /// <summary>ETag source (api-strategy §8). Incremented by the application on every change.</summary>
    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; protected init; }

    public Guid? CreatedBy { get; protected init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Touch(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>catalog.units — unit of measure; <c>code</c> unique per tenant.</summary>
internal sealed class Unit : CatalogEntity
{
    private Unit()
    {
    }

    public string Code { get; private init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool AllowsFraction { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public static Unit Create(Guid tenantId, string code, string name, bool allowsFraction, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Code = code,
        Name = name,
        AllowsFraction = allowsFraction,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>catalog.brands — name unique per tenant (case-insensitive).</summary>
internal sealed class Brand : CatalogEntity
{
    private Brand()
    {
    }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset? ArchivedAt { get; set; }

    public static Brand Create(Guid tenantId, string name, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Name = name,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>
/// catalog.categories — tree with a materialized path (<c>/rootId/childId/</c>). Depth ≤ <see cref="MaxDepth"/>
/// (PRD CAT-11); names are unique among siblings. The parent is fixed at creation, so the tree cannot form a cycle.
/// </summary>
internal sealed class Category : CatalogEntity
{
    public const int MaxDepth = 4;

    private Category()
    {
    }

    public Guid? ParentId { get; private init; }

    public string Name { get; set; } = string.Empty;

    public string Path { get; private init; } = string.Empty;

    /// <summary>1 for a root category.</summary>
    public int Depth { get; private init; }

    public int SortOrder { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public static Category CreateRoot(Guid tenantId, string name, int sortOrder, Guid userId, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new Category
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
            Path = $"/{id:D}/",
            Depth = 1,
            SortOrder = sortOrder,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }

    /// <summary>Returns null when the child would exceed <see cref="MaxDepth"/>.</summary>
    public Category? CreateChild(string name, int sortOrder, Guid userId, DateTimeOffset now)
    {
        if (Depth >= MaxDepth)
        {
            return null;
        }

        var id = Guid.CreateVersion7();
        return new Category
        {
            Id = id,
            TenantId = TenantId,
            ParentId = Id,
            Name = name,
            Path = $"{Path}{id:D}/",
            Depth = Depth + 1,
            SortOrder = sortOrder,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }
}

/// <summary>
/// catalog.products — aggregate root. Sellable/stockable identity is the <see cref="ProductVariant"/>; a simple
/// product has exactly one variant. <c>attributes</c> holds tenant-defined custom fields (data-model §4).
/// </summary>
internal sealed class Product : CatalogEntity
{
    private Product()
    {
    }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? BrandId { get; set; }

    public Guid DefaultUnitId { get; set; }

    public decimal TaxRate { get; set; }

    public string Status { get; private set; } = CatalogStatuses.Active;

    /// <summary>JSON object (jsonb) of custom field values, validated against the tenant's product field definitions.</summary>
    public string Attributes { get; set; } = "{}";

    public DateTimeOffset? ArchivedAt { get; private set; }

    public static Product Create(
        Guid tenantId,
        string name,
        string? description,
        Guid? categoryId,
        Guid? brandId,
        Guid defaultUnitId,
        decimal taxRate,
        string status,
        Guid userId,
        DateTimeOffset now)
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            CategoryId = categoryId,
            BrandId = brandId,
            DefaultUnitId = defaultUnitId,
            TaxRate = taxRate,
            CreatedAt = now,
            CreatedBy = userId,
        };
        product.SetStatus(status, now);
        return product;
    }

    public void SetStatus(string status, DateTimeOffset now)
    {
        if (!CatalogStatuses.All.Contains(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown catalog status.");
        }

        Status = status;
        ArchivedAt = status == CatalogStatuses.Archived ? ArchivedAt ?? now : null;
    }
}

/// <summary>
/// catalog.product_variants — the stock-keeping identity. Inventory, Orders, Pricing and Warehouse reference
/// <c>variant_id</c> (never product id or SKU). SKU is unique per tenant including archived rows; barcode is unique
/// per tenant when present.
/// </summary>
internal sealed class ProductVariant : CatalogEntity
{
    private ProductVariant()
    {
    }

    public Guid ProductId { get; private init; }

    public string Sku { get; set; } = string.Empty;

    public string? Barcode { get; set; }

    public string? Name { get; set; }

    /// <summary>JSON object of option name → value, e.g. <c>{"renk":"kırmızı"}</c>.</summary>
    public string OptionValues { get; set; } = "{}";

    public Guid UnitId { get; set; }

    public string Status { get; private set; } = CatalogStatuses.Active;

    /// <summary>JSON object (jsonb) of custom field values, validated against the tenant's variant field definitions.</summary>
    public string Attributes { get; set; } = "{}";

    public DateTimeOffset? ArchivedAt { get; private set; }

    public static ProductVariant Create(
        Product product,
        string sku,
        string? barcode,
        string? name,
        string optionValues,
        Guid unitId,
        string status,
        Guid userId,
        DateTimeOffset now)
    {
        var variant = new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = product.TenantId,
            ProductId = product.Id,
            Sku = sku,
            Barcode = barcode,
            Name = name,
            OptionValues = optionValues,
            UnitId = unitId,
            CreatedAt = now,
            CreatedBy = userId,
        };
        variant.SetStatus(status, now);
        return variant;
    }

    public void SetStatus(string status, DateTimeOffset now)
    {
        if (!CatalogStatuses.All.Contains(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown catalog status.");
        }

        Status = status;
        ArchivedAt = status == CatalogStatuses.Archived ? ArchivedAt ?? now : null;
    }
}

/// <summary>Which catalog record a custom field belongs to.</summary>
internal static class CustomFieldEntityTypes
{
    public const string Product = "product";
    public const string Variant = "variant";

    public static readonly IReadOnlyList<string> All = [Product, Variant];
}

/// <summary>Value types of a custom field (PRD CAT-05). The type is fixed once the field exists.</summary>
internal static class CustomFieldDataTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string Enum = "enum";
    public const string MultiEnum = "multi_enum";
    public const string Range = "range";
    public const string Date = "date";

    public static readonly IReadOnlyList<string> All = [Text, Number, Integer, Boolean, Enum, MultiEnum, Range, Date];

    public static bool HasOptions(string dataType) => dataType is Enum or MultiEnum;

    public static bool HasUnit(string dataType) => dataType is Number or Integer or Range;
}

/// <summary>
/// catalog.custom_field_definitions — a tenant's custom field (data-model §4). Values live in the
/// <c>attributes</c> jsonb of products or variants under <see cref="Key"/>. <c>key</c>, <c>entity_type</c> and
/// <c>data_type</c> are immutable; a key is unique per tenant across products and variants (so <c>attr.&lt;key&gt;</c>
/// filters are unambiguous) and is never reused, archived or not.
/// </summary>
internal sealed class CustomFieldDefinition : CatalogEntity
{
    private CustomFieldDefinition()
    {
    }

    public string EntityType { get; private init; } = CustomFieldEntityTypes.Product;

    public string Key { get; private init; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string DataType { get; private init; } = CustomFieldDataTypes.Text;

    /// <summary>Display unit for number/integer/range fields (W, K, V, mm, ay).</summary>
    public string? Unit { get; set; }

    /// <summary>JSON array of allowed values for enum/multi_enum; <c>[]</c> otherwise.</summary>
    public string Options { get; set; } = "[]";

    public bool Required { get; set; }

    public bool Filterable { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public static CustomFieldDefinition Create(
        Guid tenantId,
        string entityType,
        string key,
        string label,
        string dataType,
        string? unit,
        string optionsJson,
        bool required,
        bool filterable,
        int sortOrder,
        Guid userId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EntityType = entityType,
            Key = key,
            Label = label,
            DataType = dataType,
            Unit = unit,
            Options = optionsJson,
            Required = required,
            Filterable = filterable,
            SortOrder = sortOrder,
            CreatedAt = now,
            CreatedBy = userId,
        };
}

/// <summary>Value normalisation shared by writes and lookups (domain-model §0: <c>Sku</c>, <c>Barcode</c>).</summary>
internal static class CatalogCodes
{
    /// <summary>SKU: trimmed, upper-case invariant. Unique per tenant on the normalised value.</summary>
    public static string NormalizeSku(string sku) => sku.Trim().ToUpperInvariant();

    /// <summary>Barcode: trimmed, kept verbatim otherwise (EAN/UPC digits or internal codes).</summary>
    public static string? NormalizeBarcode(string? barcode) =>
        string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();

    /// <summary>Unit code: trimmed, lower-case invariant (adet, kg, koli).</summary>
    public static string NormalizeUnitCode(string code) => code.Trim().ToLowerInvariant();

    public static bool IsValidCode(string value) =>
        value.Length is >= 1 and <= 64 && value.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
}
