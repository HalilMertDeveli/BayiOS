using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Domain;

namespace BayiOS.UnitTests.Catalog;

public class SectorTemplateTests
{
    public static TheoryData<string> Codes() => new(SectorTemplates.All.Select(t => t.Code));

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_template_field_passes_the_api_definition_rules(string code)
    {
        var template = SectorTemplates.Find(code)!;

        foreach (var field in template.Fields)
        {
            var errors = new Dictionary<string, string[]>();
            CustomFieldService.ValidateDefinition(errors, field.EntityType, field.Key, field.Label, field.DataType, field.Unit, field.Options, 0);
            Assert.True(errors.Count == 0, $"{code}.{field.Key}: {string.Join("; ", errors.Select(e => $"{e.Key}={e.Value[0]}"))}");
        }

        Assert.Equal(template.Fields.Count, template.Fields.Select(f => f.Key).Distinct().Count());
        Assert.Equal(template.Units.Count, template.Units.Select(u => u.Code).Distinct().Count());
        Assert.All(template.Units, u => Assert.Equal(CatalogCodes.NormalizeUnitCode(u.Code), u.Code));
        Assert.Equal(template.Categories.Count, template.Categories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Template_codes_match_tenant_sectors()
    {
        Assert.Equal(["motorcycle_parts", "led_electronics", "general"], SectorTemplates.All.Select(t => t.Code));
    }

    [Theory]
    [InlineData("guc", true)]
    [InlineData("model_yili_2", true)]
    [InlineData("Guc", false)]
    [InlineData("2guc", false)]
    [InlineData("güç", false)]
    [InlineData("guc\n", false)]
    [InlineData("a.b", false)]
    public void Field_keys_are_lower_snake_case_ascii(string key, bool valid)
    {
        var errors = new Dictionary<string, string[]>();
        CustomFieldService.ValidateDefinition(errors, "product", key, "Etiket", "text", null, null, 0);

        Assert.Equal(valid, !errors.ContainsKey("key"));
    }
}
