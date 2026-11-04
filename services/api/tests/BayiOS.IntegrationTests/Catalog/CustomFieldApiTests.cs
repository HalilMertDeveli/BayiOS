using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;

namespace BayiOS.IntegrationTests.Catalog;

#pragma warning disable CA1861 // Inline request payloads read better than hoisted arrays; tests are not hot paths.

/// <summary>
/// Sector templates, custom field definitions, attribute validation and <c>attr.*</c> filters (#14, #19) with
/// synthetic motorcycle-part and LED data (sector-scenarios.md §1.2, §2.2).
/// </summary>
public class CustomFieldApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Sector templates ----

    [Fact]
    public async Task Motor_template_creates_fields_units_and_categories_and_reapplying_is_a_no_op()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");

        var templates = await owner.OkAsync<ListResponse<SectorTemplateResponse>>(await owner.GetAsync("/api/v1/sector-templates"));
        Assert.Equal(["motorcycle_parts", "led_electronics", "general"], templates.Items.Select(t => t.Code));

        var first = await ApplyAsync(owner, "motorcycle_parts");
        Assert.Equal(["adet", "takim", "kutu", "litre", "metre"], first.UnitsCreated);
        Assert.Equal(10, first.CategoriesCreated.Count);
        Assert.Equal(["uyumlu_markalar", "uyumlu_model", "model_yili", "oem_no", "taraf", "garanti_ay", "parca_tipi"], first.FieldsCreated);
        Assert.Equal(0, first.Skipped);

        var second = await ApplyAsync(owner, "motorcycle_parts");
        Assert.Empty(second.UnitsCreated);
        Assert.Empty(second.CategoriesCreated);
        Assert.Empty(second.FieldsCreated);
        Assert.Equal(5 + 10 + 7, second.Skipped);

        var fields = await owner.OkAsync<ListResponse<CustomFieldResponse>>(await owner.GetAsync("/api/v1/custom-fields"));
        Assert.Equal(7, fields.Items.Count);
        var markalar = Assert.Single(fields.Items, f => f.Key == "uyumlu_markalar");
        Assert.Equal(("product", "multi_enum", true), (markalar.EntityType, markalar.DataType, markalar.Filterable));
        Assert.Contains("Honda", markalar.Options);
        Assert.Equal("variant", Assert.Single(fields.Items, f => f.Key == "parca_tipi").EntityType);
        Assert.Equal("ay", Assert.Single(fields.Items, f => f.Key == "garanti_ay").Unit);
        Assert.Single((await owner.OkAsync<ListResponse<CustomFieldResponse>>(await owner.GetAsync("/api/v1/custom-fields?entityType=variant"))).Items);

        var units = await owner.OkAsync<ListResponse<UnitResponse>>(await owner.GetAsync("/api/v1/units"));
        Assert.Equal(5, units.Items.Count);
        Assert.True(Assert.Single(units.Items, u => u.Code == "litre").AllowsFraction);
        var categories = await owner.OkAsync<ListResponse<CategoryResponse>>(await owner.GetAsync("/api/v1/categories"));
        Assert.Equal(10, categories.Items.Count);
    }

    [Fact]
    public async Task Led_template_skips_what_the_tenant_already_has()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");
        await owner.CreateUnitAsync("adet", "Adet");
        await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "led modül" });
        var custom = await owner.CreatedAsync<CustomFieldResponse>(
            "/api/v1/custom-fields", new { entityType = "product", key = "guc", label = "Güç (kendi tanımım)", dataType = "integer", unit = "W" });

        var result = await ApplyAsync(owner, "led_electronics");

        Assert.Equal(["kutu", "koli", "metre"], result.UnitsCreated);
        Assert.DoesNotContain("LED Modül", result.CategoriesCreated);
        Assert.Equal(7, result.CategoriesCreated.Count);
        Assert.DoesNotContain("guc", result.FieldsCreated);
        Assert.Equal(11, result.FieldsCreated.Count);
        Assert.Equal(3, result.Skipped);

        // The tenant's own definition is never overwritten.
        var guc = Assert.Single((await owner.OkAsync<ListResponse<CustomFieldResponse>>(await owner.GetAsync("/api/v1/custom-fields"))).Items, f => f.Key == "guc");
        Assert.Equal((custom.Id, "Güç (kendi tanımım)", "integer", 1L), (guc.Id, guc.Label, guc.DataType, guc.Version));
    }

    [Fact]
    public async Task Unknown_template_is_404()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);

        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/sector-templates/bakery/apply", new { }), HttpStatusCode.NotFound, "resource.not_found");
    }

    // ---- Definitions ----

    [Fact]
    public async Task Custom_field_crud_with_validation_conflicts_and_etags()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");

        var create = await owner.PostAsync("/api/v1/custom-fields", new
        {
            entityType = "product",
            key = "ip_sinifi",
            label = " IP sınıfı ",
            dataType = "enum",
            options = new[] { "IP20", "IP65" },
            filterable = true,
            sortOrder = 5,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal("W/\"1\"", create.Headers.ETag?.ToString());
        var field = (await create.Content.ReadFromJsonAsync<CustomFieldResponse>(Ct))!;
        Assert.Equal(("IP sınıfı", false, true, 5), (field.Label, field.Required, field.Filterable, field.SortOrder));
        Assert.Equal($"/api/v1/custom-fields/{field.Id}", create.Headers.Location?.OriginalString);

        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/custom-fields", new { entityType = "variant", key = "ip_sinifi", label = "Tekrar", dataType = "text" }),
            HttpStatusCode.Conflict,
            "catalog.field_key_taken");

        var invalid = await ValidationErrorsAsync(await owner.PostAsync("/api/v1/custom-fields", new
        {
            entityType = "customer",
            key = "Güç W",
            label = string.Empty,
            dataType = "text",
            unit = "W",
            options = new[] { "x" },
        }));
        Assert.Equal(["entityType", "key", "label", "options", "unit"], invalid.Keys.Order());
        var enumWithoutOptions = await ValidationErrorsAsync(await owner.PostAsync("/api/v1/custom-fields", new { entityType = "product", key = "renk", label = "Renk", dataType = "enum" }));
        Assert.Equal(["options"], enumWithoutOptions.Keys);
        var duplicateOptions = await ValidationErrorsAsync(await owner.PostAsync(
            "/api/v1/custom-fields", new { entityType = "product", key = "renk", label = "Renk", dataType = "multi_enum", options = new[] { "a", "a" } }));
        Assert.Equal(["options"], duplicateOptions.Keys);
        var badType = await ValidationErrorsAsync(await owner.PostAsync("/api/v1/custom-fields", new { entityType = "product", key = "renk", label = "Renk", dataType = "color" }));
        Assert.Equal(["dataType"], badType.Keys);

        // Patch: If-Match required; key/dataType are not patchable members.
        var body = new { label = "IP koruma sınıfı", options = new[] { "IP20", "IP65", "IP67" }, required = true };
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/custom-fields/{field.Id}", body, ifMatch: null), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/custom-fields/{field.Id}", body, "W/\"9\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        var patched = await owner.OkAsync<CustomFieldResponse>(await owner.PatchAsync($"/api/v1/custom-fields/{field.Id}", body, "W/\"1\""));
        Assert.Equal(("IP koruma sınıfı", true, 2L, "enum", "ip_sinifi"), (patched.Label, patched.Required, patched.Version, patched.DataType, patched.Key));
        Assert.Equal(["IP20", "IP65", "IP67"], patched.Options);
        var emptyOptions = await ValidationErrorsAsync(await owner.PatchAsync($"/api/v1/custom-fields/{field.Id}", new { options = Array.Empty<string>() }, "W/\"2\""));
        Assert.Equal(["options"], emptyOptions.Keys);

        // Archive: hidden from the default list, kept with includeArchived; the key stays taken.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/custom-fields/{field.Id}", "W/\"2\"")).StatusCode);
        Assert.Empty((await owner.OkAsync<ListResponse<CustomFieldResponse>>(await owner.GetAsync("/api/v1/custom-fields"))).Items);
        Assert.True(Assert.Single((await owner.OkAsync<ListResponse<CustomFieldResponse>>(await owner.GetAsync("/api/v1/custom-fields?includeArchived=true"))).Items).IsArchived);
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/custom-fields", new { entityType = "product", key = "ip_sinifi", label = "Yeni", dataType = "text" }),
            HttpStatusCode.Conflict,
            "catalog.field_key_taken");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/custom-fields?entityType=customer"), HttpStatusCode.BadRequest, "request.malformed");
    }

    // ---- Attribute validation ----

    [Fact]
    public async Task Product_attributes_are_validated_against_definitions_with_field_level_errors()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");
        await ApplyAsync(owner, "motorcycle_parts");
        await owner.CreatedAsync<CustomFieldResponse>("/api/v1/custom-fields", new { entityType = "product", key = "uretim_tarihi", label = "Üretim tarihi", dataType = "date" });
        await owner.CreatedAsync<CustomFieldResponse>("/api/v1/custom-fields", new { entityType = "product", key = "orijinal_kutu", label = "Orijinal kutu", dataType = "boolean", required = true });
        var unit = await UnitAsync(owner, "takim");

        var errors = await ValidationErrorsAsync(await owner.PostAsync("/api/v1/products", new
        {
            name = "CG 125 ön balata",
            defaultUnitId = unit,
            taxRate = 20m,
            sku = "FRN-CG125-ON-X",
            attributes = new Dictionary<string, object>
            {
                ["bilinmeyen"] = "x",
                ["uyumlu_markalar"] = new[] { "Honda", "Honda" },
                ["model_yili"] = new { min = 2021, max = 2015 },
                ["taraf"] = "Orta",
                ["garanti_ay"] = 6.5,
                ["oem_no"] = 90001,
                ["uretim_tarihi"] = "2026-13-01",
                ["parca_tipi"] = "Muadil",
            },
            variantAttributes = new Dictionary<string, object> { ["parca_tipi"] = "Sahte" },
        }));

        Assert.Equal(
            [
                "attributes.bilinmeyen", "attributes.garanti_ay", "attributes.model_yili", "attributes.oem_no", "attributes.orijinal_kutu",
                "attributes.parca_tipi", "attributes.taraf", "attributes.uretim_tarihi", "attributes.uyumlu_markalar", "variantAttributes.parca_tipi",
            ],
            errors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Required.", errors["attributes.orijinal_kutu"][0]);
        Assert.StartsWith("Unknown custom field for a product", errors["attributes.parca_tipi"][0], StringComparison.Ordinal);

        // Valid values are stored normalised (text trimmed) and returned.
        var product = await owner.CreatedAsync<ProductResponse>("/api/v1/products", new
        {
            name = "CG 125 ön balata",
            defaultUnitId = unit,
            taxRate = 20m,
            sku = "FRN-CG125-ON-OR",
            attributes = new Dictionary<string, object>
            {
                ["uyumlu_markalar"] = new[] { "Honda" },
                ["uyumlu_model"] = "  CG 125 ",
                ["model_yili"] = new { min = 2015, max = 2021 },
                ["oem_no"] = "90001-SNT-001",
                ["taraf"] = "Ön",
                ["garanti_ay"] = 6,
                ["uretim_tarihi"] = "2026-01-15",
                ["orijinal_kutu"] = true,
            },
            variantAttributes = new Dictionary<string, object> { ["parca_tipi"] = "Orijinal" },
        });
        Assert.Equal("CG 125", product.Attributes["uyumlu_model"].GetString());
        Assert.Equal(2021, product.Attributes["model_yili"].GetProperty("max").GetInt32());
        Assert.Equal("Orijinal", Assert.Single(product.Variants).Attributes["parca_tipi"].GetString());

        // Merge patch: set, remove (null) and the required check on the merged result.
        var removeRequired = await ValidationErrorsAsync(await owner.PatchAsync(
            $"/api/v1/products/{product.Id}", new Dictionary<string, object?> { ["attributes"] = new Dictionary<string, object?> { ["orijinal_kutu"] = null } }, "W/\"1\""));
        Assert.Equal(["attributes.orijinal_kutu"], removeRequired.Keys);
        var patched = await owner.OkAsync<ProductResponse>(await owner.PatchAsync(
            $"/api/v1/products/{product.Id}",
            new Dictionary<string, object?> { ["attributes"] = new Dictionary<string, object?> { ["taraf"] = "Arka", ["oem_no"] = null } },
            "W/\"1\""));
        Assert.Equal("Arka", patched.Attributes["taraf"].GetString());
        Assert.False(patched.Attributes.ContainsKey("oem_no"));
        Assert.Equal(8 - 1, patched.Attributes.Count);

        // A plain patch keeps attributes untouched.
        var renamed = await owner.OkAsync<ProductResponse>(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { name = "CG 125 arka balata" }, "W/\"2\""));
        Assert.Equal(7, renamed.Attributes.Count);

        // Variant attributes: add a variant (muadil) and patch it.
        var variantErrors = await ValidationErrorsAsync(await owner.PostAsync(
            $"/api/v1/products/{product.Id}/variants", new { sku = "FRN-CG125-ON-MU", attributes = new Dictionary<string, object> { ["taraf"] = "Ön" } }));
        Assert.Equal(["attributes.taraf"], variantErrors.Keys);
        var muadil = await owner.CreatedAsync<VariantResponse>(
            $"/api/v1/products/{product.Id}/variants", new { sku = "FRN-CG125-ON-MU", attributes = new Dictionary<string, object> { ["parca_tipi"] = "Muadil" } });
        var yanSanayi = await owner.OkAsync<VariantResponse>(await owner.PatchAsync(
            $"/api/v1/variants/{muadil.Id}", new Dictionary<string, object?> { ["attributes"] = new Dictionary<string, object?> { ["parca_tipi"] = "Yan sanayi" } }, "W/\"1\""));
        Assert.Equal("Yan sanayi", yanSanayi.Attributes["parca_tipi"].GetString());
        var wrongShape = await ValidationErrorsAsync(await owner.PatchAsync($"/api/v1/variants/{muadil.Id}", new { attributes = "Muadil" }, "W/\"2\""));
        Assert.Equal(["attributes"], wrongShape.Keys);
    }

    [Fact]
    public async Task Archived_fields_are_rejected_on_write_but_stored_values_are_kept()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");
        var field = await owner.CreatedAsync<CustomFieldResponse>("/api/v1/custom-fields", new { entityType = "product", key = "guc", label = "Güç", dataType = "number", unit = "W" });
        var unit = await owner.CreateUnitAsync();
        var product = await owner.CreatedAsync<ProductResponse>("/api/v1/products", new
        {
            name = "P3.91 iç mekân modül",
            defaultUnitId = unit.Id,
            taxRate = 20m,
            sku = "LED-P391-IC",
            attributes = new Dictionary<string, object> { ["guc"] = 40.5 },
        });
        Assert.Equal(40.5m, product.Attributes["guc"].GetDecimal());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/custom-fields/{field.Id}", "W/\"1\"")).StatusCode);

        var rejected = await ValidationErrorsAsync(await owner.PatchAsync(
            $"/api/v1/products/{product.Id}", new { attributes = new Dictionary<string, object> { ["guc"] = 50 } }, "W/\"1\""));
        Assert.Equal("Custom field is archived.", rejected["attributes.guc"][0]);
        var kept = await owner.OkAsync<ProductResponse>(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { attributes = new { } }, "W/\"1\""));
        Assert.Equal(40.5m, kept.Attributes["guc"].GetDecimal());
    }

    // ---- attr.* filters ----

    [Fact]
    public async Task Motor_attribute_filters_use_product_and_variant_fields()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");
        await ApplyAsync(owner, "motorcycle_parts");
        var unit = await UnitAsync(owner, "takim");

        var cgOn = await MotorProductAsync(owner, unit, "CG 125 ön balata", "FRN-CG125-ON", ["Honda"], "CG 125", 2015, 2021, "Ön", ["Orijinal", "Muadil"]);
        var nmaxArka = await MotorProductAsync(owner, unit, "NMAX arka balata", "FRN-NMAX-ARKA", ["Yamaha"], "NMAX 155", 2019, 2024, "Arka", ["Yan sanayi"]);
        var pulsar = await MotorProductAsync(owner, unit, "Pulsar zincir seti", "AKT-NS200-ZNC", ["Bajaj", "Kuba"], "Pulsar NS200", 2012, 2018, "Arka", ["Muadil"]);

        Assert.Equal([cgOn], await FilterAsync(owner, "attr.uyumlu_markalar=Honda"));
        Assert.Equal([pulsar], await FilterAsync(owner, "attr.uyumlu_markalar=Kuba"));
        Assert.Equal([cgOn, pulsar], await FilterAsync(owner, "attr.model_yili=2016"));
        Assert.Equal([cgOn, nmaxArka], await FilterAsync(owner, "attr.model_yili=2020"));
        Assert.Equal([nmaxArka, pulsar], await FilterAsync(owner, "attr.taraf=Arka"));
        Assert.Equal([pulsar], await FilterAsync(owner, "attr.taraf=Arka&attr.model_yili=2018"));
        Assert.Equal([cgOn, pulsar], await FilterAsync(owner, "attr.parca_tipi=Muadil"));
        Assert.Equal([pulsar], await FilterAsync(owner, "attr.parca_tipi=Muadil&attr.uyumlu_model=Pulsar%20NS200"));
        Assert.Empty(await FilterAsync(owner, "attr.parca_tipi=Orijinal&attr.taraf=Arka"));
        Assert.Equal([cgOn], await FilterAsync(owner, "attr.parca_tipi=Orijinal&q=balata"));

        // Not filterable / unknown / malformed → 400.
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.oem_no=90001-SNT-001"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.bilinmeyen=x"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.model_yili=yeni"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.taraf.min=1"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.taraf=On&attr.taraf=Arka"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Led_numeric_filters_support_equality_and_bounds()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");
        await ApplyAsync(owner, "led_electronics");
        var unit = await UnitAsync(owner, "adet");

        var ic = await LedProductAsync(owner, unit, "P2.5 iç mekân modül", "LED-P25-IC", guc: 30, kelvin: 6500, ip: "IP20", pitch: 2.5m, kullanim: "İç mekân");
        var dis = await LedProductAsync(owner, unit, "P4 dış mekân modül", "LED-P4-DIS", guc: 45.5m, kelvin: 6500, ip: "IP65", pitch: 4m, kullanim: "Dış mekân");
        var yari = await LedProductAsync(owner, unit, "P10 yarı dış modül", "LED-P10-YD", guc: 60, kelvin: 3000, ip: "IP54", pitch: 10m, kullanim: "Yarı dış");

        Assert.Equal([ic, dis], await FilterAsync(owner, "attr.renk_sicakligi=6500"));
        Assert.Equal([dis], await FilterAsync(owner, "attr.ip_sinifi=IP65"));
        Assert.Equal([dis], await FilterAsync(owner, "attr.pixel_pitch=4.0"));
        Assert.Equal([dis, yari], await FilterAsync(owner, "attr.guc.min=45"));
        Assert.Equal([ic, dis], await FilterAsync(owner, "attr.guc.max=45.5"));
        Assert.Equal([dis], await FilterAsync(owner, "attr.guc.min=31&attr.guc.max=59"));
        Assert.Equal([ic], await FilterAsync(owner, "attr.kullanim=%C4%B0%C3%A7%20mek%C3%A2n"));
        Assert.Equal([yari], await FilterAsync(owner, "attr.renk_sicakligi=3000&attr.guc.min=50&sort=name"));
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.seri_no_takibi=true"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?attr.guc.min=kırk"), HttpStatusCode.BadRequest, "request.malformed");
    }

    // ---- Permissions and isolation ----

    [Fact]
    public async Task Only_fields_managers_can_change_definitions_or_apply_templates()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");
        var field = await owner.CreatedAsync<CustomFieldResponse>("/api/v1/custom-fields", new { entityType = "product", key = "voltaj", label = "Voltaj", dataType = "number", unit = "V" });
        var admin = await owner.AddMemberAsync(fixture, "admin");
        var manager = await owner.AddMemberAsync(fixture, "manager");
        var viewer = await owner.AddMemberAsync(fixture, "viewer");

        foreach (var denied in new[] { manager, viewer })
        {
            Assert.Single((await denied.OkAsync<ListResponse<CustomFieldResponse>>(await denied.GetAsync("/api/v1/custom-fields"))).Items);
            Assert.Equal(HttpStatusCode.OK, (await denied.GetAsync("/api/v1/sector-templates")).StatusCode);
            await HttpAssert.ProblemAsync(
                await denied.PostAsync("/api/v1/custom-fields", new { entityType = "product", key = "watt", label = "W", dataType = "number" }), HttpStatusCode.Forbidden, "auth.forbidden");
            await HttpAssert.ProblemAsync(await denied.PatchAsync($"/api/v1/custom-fields/{field.Id}", new { label = "X" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
            await HttpAssert.ProblemAsync(await denied.DeleteAsync($"/api/v1/custom-fields/{field.Id}", "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
            await HttpAssert.ProblemAsync(await denied.PostAsync("/api/v1/sector-templates/led_electronics/apply", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/v1/sector-templates/led_electronics/apply", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/v1/custom-fields/{field.Id}", new { label = "Gerilim" }, "W/\"1\"")).StatusCode);
    }

    [Fact]
    public async Task Definitions_and_filters_do_not_cross_tenants()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");
        var b = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");
        var fieldA = await a.CreatedAsync<CustomFieldResponse>("/api/v1/custom-fields", new { entityType = "product", key = "ip_sinifi", label = "IP", dataType = "enum", options = new[] { "IP65" }, filterable = true });
        await ApplyAsync(b, "motorcycle_parts");

        // Same key in another tenant is independent; B cannot see, change or archive A's definition.
        var fieldB = await b.CreatedAsync<CustomFieldResponse>("/api/v1/custom-fields", new { entityType = "variant", key = "ip_sinifi", label = "IP (B)", dataType = "text" });
        Assert.NotEqual(fieldA.Id, fieldB.Id);
        Assert.DoesNotContain((await b.OkAsync<ListResponse<CustomFieldResponse>>(await b.GetAsync("/api/v1/custom-fields?includeArchived=true"))).Items, f => f.Id == fieldA.Id);
        await HttpAssert.ProblemAsync(await b.PatchAsync($"/api/v1/custom-fields/{fieldA.Id}", new { label = "hacked" }, "W/\"1\""), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.DeleteAsync($"/api/v1/custom-fields/{fieldA.Id}", "W/\"1\""), HttpStatusCode.NotFound, "resource.not_found");

        // B's text field is not filterable; A's filterable key means nothing in B.
        await HttpAssert.ProblemAsync(await b.GetAsync("/api/v1/products?attr.ip_sinifi=IP65"), HttpStatusCode.BadRequest, "request.malformed");
        Assert.DoesNotContain((await b.OkAsync<ListResponse<CustomFieldResponse>>(await b.GetAsync("/api/v1/custom-fields"))).Items, f => f.Key == "guc");

        // Targeting A with B's credentials is denied before any catalog code runs.
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).GetAsync("/api/v1/custom-fields"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).PostAsync("/api/v1/sector-templates/led_electronics/apply", new { }), HttpStatusCode.Forbidden, "tenant.access_denied");

        var aFields = await a.OkAsync<ListResponse<CustomFieldResponse>>(await a.GetAsync("/api/v1/custom-fields"));
        Assert.Equal(("IP", 1L), (Assert.Single(aFields.Items).Label, aFields.Items[0].Version));
    }

    // ---- Helpers ----

    private static async Task<ApplySectorTemplateResponse> ApplyAsync(CatalogClient client, string code) =>
        await client.OkAsync<ApplySectorTemplateResponse>(await client.PostAsync($"/api/v1/sector-templates/{code}/apply", new { }));

    private static async Task<Guid> UnitAsync(CatalogClient client, string code) =>
        Assert.Single((await client.OkAsync<ListResponse<UnitResponse>>(await client.GetAsync("/api/v1/units"))).Items, u => u.Code == code).Id;

    private static async Task<List<Guid>> FilterAsync(CatalogClient client, string query)
    {
        var page = await client.OkAsync<PagedResponse<ProductResponse>>(await client.GetAsync("/api/v1/products?" + query));
        return page.Items.OrderBy(p => p.CreatedAt).Select(p => p.Id).ToList();
    }

    private static async Task<Guid> MotorProductAsync(
        CatalogClient client, Guid unit, string name, string sku, string[] markalar, string model, int from, int to, string taraf, string[] parcaTipleri)
    {
        var product = await client.CreatedAsync<ProductResponse>("/api/v1/products", new
        {
            name,
            defaultUnitId = unit,
            taxRate = 20m,
            attributes = new Dictionary<string, object>
            {
                ["uyumlu_markalar"] = markalar,
                ["uyumlu_model"] = model,
                ["model_yili"] = new { min = from, max = to },
                ["taraf"] = taraf,
            },
            variants = parcaTipleri.Select((tip, i) => new
            {
                sku = $"{sku}-{i}",
                name = tip,
                attributes = new Dictionary<string, object> { ["parca_tipi"] = tip },
            }),
        });
        return product.Id;
    }

    private static async Task<Guid> LedProductAsync(
        CatalogClient client, Guid unit, string name, string sku, decimal guc, int kelvin, string ip, decimal pitch, string kullanim)
    {
        var product = await client.CreatedAsync<ProductResponse>("/api/v1/products", new
        {
            name,
            defaultUnitId = unit,
            taxRate = 20m,
            sku,
            attributes = new Dictionary<string, object>
            {
                ["guc"] = guc,
                ["renk_sicakligi"] = kelvin,
                ["voltaj"] = 5,
                ["ip_sinifi"] = ip,
                ["pixel_pitch"] = pitch,
                ["kullanim"] = kullanim,
                ["seri_no_takibi"] = false,
            },
        });
        return product.Id;
    }

    private static async Task<Dictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        return document.RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
