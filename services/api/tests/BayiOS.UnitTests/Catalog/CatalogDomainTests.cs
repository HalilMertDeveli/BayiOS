using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Domain;

namespace BayiOS.UnitTests.Catalog;

public class CatalogDomainTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(" frn-cg125-on-or ", "FRN-CG125-ON-OR")]
    [InlineData("led-p10-rgb", "LED-P10-RGB")]
    [InlineData("ABC 1", "ABC 1")]
    public void Sku_is_trimmed_and_upper_cased(string input, string expected) =>
        Assert.Equal(expected, CatalogCodes.NormalizeSku(input));

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" 8690000000017 ", "8690000000017")]
    public void Barcode_is_trimmed_and_blank_means_none(string? input, string? expected) =>
        Assert.Equal(expected, CatalogCodes.NormalizeBarcode(input));

    [Fact]
    public void Category_tree_builds_materialized_paths_and_stops_at_depth_four()
    {
        var root = Category.CreateRoot(Tenant, "Fren", 0, User, Now);
        var level2 = root.CreateChild("Balata", 0, User, Now)!;
        var level3 = level2.CreateChild("Ön", 0, User, Now)!;
        var level4 = level3.CreateChild("Orijinal", 0, User, Now)!;

        Assert.Equal($"/{root.Id}/", root.Path);
        Assert.Equal($"/{root.Id}/{level2.Id}/", level2.Path);
        Assert.Equal((4, level3.Id, Tenant), (level4.Depth, level4.ParentId!.Value, level4.TenantId));
        Assert.StartsWith(level3.Path, level4.Path, StringComparison.Ordinal);
        Assert.Null(level4.CreateChild("Beşinci", 0, User, Now));
    }

    [Fact]
    public void Product_archive_sets_archived_at_and_restore_clears_it()
    {
        var product = Product.Create(Tenant, "Buji", null, null, null, Guid.CreateVersion7(), 20m, CatalogStatuses.Active, User, Now);
        var later = Now.AddHours(1);

        product.SetStatus(CatalogStatuses.Archived, later);
        Assert.Equal((CatalogStatuses.Archived, later), (product.Status, product.ArchivedAt!.Value));

        product.SetStatus(CatalogStatuses.Active, later);
        Assert.Null(product.ArchivedAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => product.SetStatus("deleted", later));
    }

    [Fact]
    public void Touch_increments_row_version_and_records_actor()
    {
        var product = Product.Create(Tenant, "Zincir", null, null, null, Guid.CreateVersion7(), 20m, CatalogStatuses.Active, User, Now);
        var editor = Guid.NewGuid();

        product.Touch(editor, Now.AddMinutes(5));

        Assert.Equal((2L, editor, Now.AddMinutes(5)), (product.RowVersion, product.UpdatedBy!.Value, product.UpdatedAt!.Value));
        Assert.Equal(User, product.CreatedBy);
    }

    [Fact]
    public void Variant_belongs_to_its_products_tenant()
    {
        var product = Product.Create(Tenant, "Modül", null, null, null, Guid.CreateVersion7(), 20m, CatalogStatuses.Active, User, Now);
        var variant = ProductVariant.Create(product, "LED-P10", null, null, "{}", product.DefaultUnitId, CatalogStatuses.Active, User, Now);

        Assert.Equal((Tenant, product.Id), (variant.TenantId, variant.ProductId));
        Assert.NotEqual(product.Id, variant.Id);
    }

    [Fact]
    public void Keyset_cursor_round_trips_and_is_bound_to_its_sort()
    {
        var id = Guid.CreateVersion7();
        var encoded = new KeysetCursor("name", "Aynı ad", id).Encode();

        Assert.True(KeysetCursor.TryDecode(encoded, "name", out var cursor));
        Assert.Equal(("Aynı ad", id), (cursor.Key, cursor.Id));
        Assert.False(KeysetCursor.TryDecode(encoded, "-createdAt", out _));
        Assert.False(KeysetCursor.TryDecode("%%%", "name", out _));
    }

    [Fact]
    public void Like_pattern_escapes_wildcards() =>
        Assert.Equal("%100\\%\\_a\\\\b%", CatalogText.ContainsPattern("100%_a\\b"));
}
