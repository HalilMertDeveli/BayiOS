using System.Text.Json.Nodes;
using BayiOS.Modules.Imports.Application;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Parsing;

namespace BayiOS.UnitTests.BulkImport;

/// <summary>
/// Pure rules behind #94: custom field cell syntax (before Catalog's validator), the catalog field codec the rollback
/// conflict check compares with, and the price marker in previous/applied values.
/// </summary>
public class ImportRollbackRulesTests
{
    [Theory]
    [InlineData("number", "1.234,5", "1234.5")]
    [InlineData("integer", "12", "12")]
    [InlineData("boolean", "Evet", "true")]
    [InlineData("boolean", "hayır", "false")]
    [InlineData("boolean", "belki", "\"belki\"")]
    [InlineData("date", "05.11.2026", "\"2026-11-05\"")]
    [InlineData("date", "2026-11-05", "\"2026-11-05\"")]
    [InlineData("multi_enum", "Honda; Yamaha |Bajaj, ", "[\"Honda\",\"Yamaha\",\"Bajaj\"]")]
    [InlineData("range", "2015-2020", "{\"min\":2015,\"max\":2020}")]
    [InlineData("range", "1,5 – 2,5", "{\"min\":1.5,\"max\":2.5}")]
    [InlineData("range", "2015", "\"2015\"")]
    [InlineData("enum", " Ön ", "\" Ön \"")]
    public void Cells_are_read_as_the_custom_field_shape(string dataType, string cell, string expectedJson)
    {
        var node = ImportAttributeCells.ToJson(dataType, cell);
        Assert.True(ImportCatalogFields.Same(JsonNode.Parse(expectedJson), node), $"{node?.ToJsonString()} ≠ {expectedJson}");
    }

    [Fact]
    public void Same_compares_numbers_by_value_and_treats_null_as_absent()
    {
        Assert.True(ImportCatalogFields.Same(JsonNode.Parse("18"), JsonNode.Parse("18.00")));
        Assert.True(ImportCatalogFields.Same(JsonNode.Parse("{\"min\":1,\"max\":2.0}"), JsonNode.Parse("{\"max\":2,\"min\":1.0}")));
        Assert.True(ImportCatalogFields.Same(null, null));
        Assert.False(ImportCatalogFields.Same(JsonNode.Parse("\"a\""), null));
        Assert.False(ImportCatalogFields.Same(JsonNode.Parse("[\"a\",\"b\"]"), JsonNode.Parse("[\"b\",\"a\"]")));
        Assert.False(ImportCatalogFields.Same(JsonNode.Parse("\"18\""), JsonNode.Parse("18")));
    }

    [Fact]
    public void Read_flattens_product_and_variant_attributes_next_to_the_fields()
    {
        var unit = Guid.NewGuid();
        var snapshot = new CatalogVariantSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), "SKU-1", null, unit, "active", 1, "Ad", null, 20m, null, null, "active", 3,
            "{\"taraf\":\"Ön\"}", "{\"parca_tipi\":\"Muadil\"}");
        var values = ImportCatalogFields.Read(snapshot);
        Assert.Equal("Ön", values["attr.taraf"]!.GetValue<string>());
        Assert.Equal("Muadil", values["attr.parca_tipi"]!.GetValue<string>());
        Assert.Equal(unit.ToString(), values["unitId"]!.GetValue<string>());
        Assert.True(values.ContainsKey("description"));
        Assert.Null(values["description"]);
        Assert.Equal(20m, values["taxRate"]!.GetValue<decimal>());
    }

    [Fact]
    public void Price_marker_records_a_created_item_as_null()
    {
        var created = JsonNode.Parse(ImportChunkProcessor.WithPrice("{\"name\":\"Eski\"}", null))!.AsObject();
        Assert.True(created.ContainsKey("unitPrice"));
        Assert.Null(created["unitPrice"]);
        Assert.Equal("Eski", created["name"]!.GetValue<string>());
        Assert.Equal(12.5m, JsonNode.Parse(ImportChunkProcessor.WithPrice(null, 12.5m))!["unitPrice"]!.GetValue<decimal>());
    }
}
