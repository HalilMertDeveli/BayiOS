using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;

namespace BayiOS.IntegrationTests.Catalog;

/// <summary>Catalog API (#16, #17) with synthetic motorcycle-part and LED-module samples (sector-scenarios.md).</summary>
public class CatalogApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Motor_parts_catalog_crud_happy_path()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");
        var adet = await owner.CreateUnitAsync("adet", "Adet");
        var takim = await owner.CreateUnitAsync("TAKIM", "Takım");
        Assert.Equal("takim", takim.Code);

        var fren = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Fren" });
        var balata = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Balata", parentId = fren.Id });
        Assert.Equal((2, fren.Id), (balata.Depth, balata.ParentId!.Value));
        Assert.StartsWith(fren.Path, balata.Path, StringComparison.Ordinal);
        var brand = await owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Sentetik Moto" });

        var create = await owner.PostAsync("/api/v1/products", new
        {
            name = "CG 125 ön fren balatası (orijinal)",
            description = "Sentetik test ürünü",
            categoryId = balata.Id,
            brandId = brand.Id,
            defaultUnitId = takim.Id,
            taxRate = 20m,
            sku = " frn-cg125-on-or ",
            barcode = "8690000000017",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal("W/\"1\"", create.Headers.ETag?.ToString());
        var product = (await create.Content.ReadFromJsonAsync<ProductResponse>(Ct))!;
        Assert.Equal($"/api/v1/products/{product.Id}", create.Headers.Location?.OriginalString);
        var variant = Assert.Single(product.Variants);
        Assert.Equal(("FRN-CG125-ON-OR", "8690000000017", takim.Id), (variant.Sku, variant.Barcode, variant.UnitId));
        Assert.Equal((owner.UserId, "active"), (product.CreatedBy!.Value, product.Status));
        Assert.Empty(product.Attributes);

        // Read back with ETag.
        var get = await owner.GetAsync($"/api/v1/products/{product.Id}");
        Assert.Equal("W/\"1\"", get.Headers.ETag?.ToString());
        Assert.Equal(product.Id, (await owner.OkAsync<ProductResponse>(get)).Id);

        // Merge patch: rename, clear brand, deactivate.
        var patched = await owner.OkAsync<ProductResponse>(await owner.PatchAsync(
            $"/api/v1/products/{product.Id}", new { name = "CG 125 ön balata", brandId = (Guid?)null, status = "inactive" }, "W/\"1\""));
        Assert.Equal(("CG 125 ön balata", (Guid?)null, "inactive", 2L), (patched.Name, patched.BrandId, patched.Status, patched.Version));
        Assert.Equal(balata.Id, patched.CategoryId);
        Assert.Equal(owner.UserId, patched.UpdatedBy);
        Assert.NotNull(patched.UpdatedAt);

        // Second variant (muadil), unit defaults to the product's unit.
        var muadil = await owner.CreatedAsync<VariantResponse>($"/api/v1/products/{product.Id}/variants", new { sku = "FRN-CG125-ON-MU", name = "Muadil" });
        Assert.Equal(takim.Id, muadil.UnitId);
        var updatedVariant = await owner.OkAsync<VariantResponse>(await owner.PatchAsync(
            $"/api/v1/variants/{muadil.Id}", new { barcode = "8690000000024", unitId = adet.Id, optionValues = new Dictionary<string, string> { ["kalite"] = "muadil" } }, "W/\"1\""));
        Assert.Equal(("8690000000024", adet.Id, "muadil"), (updatedVariant.Barcode, updatedVariant.UnitId, updatedVariant.OptionValues["kalite"]));

        // Barcode scan.
        var scanned = await owner.OkAsync<VariantResponse>(await owner.GetAsync("/api/v1/variants/lookup?barcode=8690000000024"));
        Assert.Equal(muadil.Id, scanned.Id);
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/variants/lookup?barcode=0000000000000"), HttpStatusCode.NotFound, "resource.not_found");

        // Archive a variant, then the product; archived products leave the default list.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/variants/{muadil.Id}", "W/\"2\"")).StatusCode);
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/variants/{variant.Id}", "W/\"1\""), HttpStatusCode.Conflict, "catalog.last_variant");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/products/{product.Id}", "W/\"2\"")).StatusCode);

        var archived = await owner.OkAsync<ProductResponse>(await owner.GetAsync($"/api/v1/products/{product.Id}"));
        Assert.Equal(("archived", 3L), (archived.Status, archived.Version));
        Assert.Empty((await owner.OkAsync<PagedResponse<ProductResponse>>(await owner.GetAsync("/api/v1/products"))).Items);
        Assert.Single((await owner.OkAsync<PagedResponse<ProductResponse>>(await owner.GetAsync("/api/v1/products?status=archived"))).Items);

        // Restore.
        var restored = await owner.OkAsync<ProductResponse>(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { status = "active" }, "W/\"3\""));
        Assert.Equal("active", restored.Status);
    }

    [Fact]
    public async Task Led_module_with_variants_and_search_filters()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "led_electronics");
        var adet = await owner.CreateUnitAsync();
        var metre = await owner.CreatedAsync<UnitResponse>("/api/v1/units", new { code = "m", name = "Metre", allowsFraction = true });
        Assert.True(metre.AllowsFraction);
        var led = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "LED Modül" });
        var serit = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Şerit LED" });
        var brandA = await owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Örnek Işık" });
        var brandB = await owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Deneme LED" });

        var module = await owner.CreatedAsync<ProductResponse>("/api/v1/products", new
        {
            name = "P10 SMD LED modül 32x16",
            categoryId = led.Id,
            brandId = brandA.Id,
            defaultUnitId = adet.Id,
            taxRate = 20m,
            variants = new object[]
            {
                new { sku = "LED-P10-RGB", barcode = "2000000000015", name = "RGB", optionValues = new { renk = "RGB" } },
                new { sku = "LED-P10-KRM", barcode = "2000000000022", name = "Kırmızı", optionValues = new { renk = "kırmızı" } },
            },
        });
        Assert.Equal(2, module.Variants.Count);
        Assert.Equal("kırmızı", module.Variants.Single(v => v.Sku == "LED-P10-KRM").OptionValues["renk"]);

        await owner.CreateSimpleProductAsync(metre.Id, "12V şerit LED 5 m", "LED-SRT-12V", categoryId: serit.Id, brandId: brandB.Id);
        await owner.CreateSimpleProductAsync(adet.Id, "Kontrol kartı (sentetik)", "KRT-CTRL-01", "2000000000039", led.Id, brandB.Id);

        async Task<string[]> Search(string query) =>
            [.. (await owner.OkAsync<PagedResponse<ProductResponse>>(await owner.GetAsync("/api/v1/products?" + query))).Items.Select(p => p.Name).Order()];

        Assert.Equal(["P10 SMD LED modül 32x16"], await Search("q=smd"));                 // name contains, case-insensitive
        Assert.Equal(["P10 SMD LED modül 32x16"], await Search("q=p10-krm"));             // SKU contains
        Assert.Equal(["Kontrol kartı (sentetik)"], await Search("q=2000000000039"));      // exact barcode
        Assert.Equal(["Kontrol kartı (sentetik)", "P10 SMD LED modül 32x16"], await Search($"categoryId={led.Id}"));
        Assert.Equal(["12V şerit LED 5 m", "Kontrol kartı (sentetik)"], await Search($"brandId={brandB.Id}"));
        Assert.Empty(await Search("q=100%25"));                                           // LIKE wildcard is escaped
        Assert.Equal(3, (await Search("sort=name")).Length);

        // Category filter includes subcategories.
        var alt = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "İç mekan", parentId = led.Id });
        await owner.CreateSimpleProductAsync(adet.Id, "P4 iç mekan modül", "LED-P4-IC", categoryId: alt.Id);
        Assert.Equal(3, (await Search($"categoryId={led.Id}")).Length);

        // Variant list and lookups.
        var variants = await owner.OkAsync<PagedResponse<VariantResponse>>(await owner.GetAsync($"/api/v1/variants?productId={module.Id}"));
        Assert.Equal(["LED-P10-KRM", "LED-P10-RGB"], variants.Items.Select(v => v.Sku));
        var bySku = await owner.OkAsync<PagedResponse<VariantResponse>>(await owner.GetAsync("/api/v1/variants?sku=led-p10-rgb"));
        Assert.Equal("2000000000015", Assert.Single(bySku.Items).Barcode);

        // Reference lists.
        Assert.Equal(["adet", "m"], (await owner.OkAsync<ListResponse<UnitResponse>>(await owner.GetAsync("/api/v1/units"))).Items.Select(u => u.Code));
        Assert.Equal(3, (await owner.OkAsync<ListResponse<CategoryResponse>>(await owner.GetAsync("/api/v1/categories"))).Items.Count);
        Assert.Single((await owner.OkAsync<ListResponse<BrandResponse>>(await owner.GetAsync("/api/v1/brands?q=deneme"))).Items);
    }

    [Fact]
    public async Task Reference_data_update_and_archive()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync("koli", "Koli");
        var brand = await owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Eski Ad" });
        var root = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Kök" });
        var child = await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Alt", parentId = root.Id });

        var renamed = await owner.OkAsync<BrandResponse>(await owner.PatchAsync($"/api/v1/brands/{brand.Id}", new { name = "Yeni Ad" }, "W/\"1\""));
        Assert.Equal(("Yeni Ad", 2L), (renamed.Name, renamed.Version));
        var unitPatched = await owner.OkAsync<UnitResponse>(await owner.PatchAsync($"/api/v1/units/{unit.Id}", new { allowsFraction = true }, "W/\"1\""));
        Assert.True(unitPatched.AllowsFraction);
        var sorted = await owner.OkAsync<CategoryResponse>(await owner.PatchAsync($"/api/v1/categories/{child.Id}", new { sortOrder = 5 }, "W/\"1\""));
        Assert.Equal(5, sorted.SortOrder);

        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/categories/{root.Id}", "W/\"1\""), HttpStatusCode.Conflict, "catalog.category_has_children");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/categories/{child.Id}", "W/\"2\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/categories/{root.Id}", "W/\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/brands/{brand.Id}", "W/\"2\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/units/{unit.Id}", "W/\"2\"")).StatusCode);

        Assert.Empty((await owner.OkAsync<ListResponse<CategoryResponse>>(await owner.GetAsync("/api/v1/categories"))).Items);
        Assert.Equal(2, (await owner.OkAsync<ListResponse<CategoryResponse>>(await owner.GetAsync("/api/v1/categories?includeArchived=true"))).Items.Count);
        Assert.Empty((await owner.OkAsync<ListResponse<BrandResponse>>(await owner.GetAsync("/api/v1/brands"))).Items);
        Assert.Empty((await owner.OkAsync<ListResponse<UnitResponse>>(await owner.GetAsync("/api/v1/units"))).Items);

        // An archived name can be reused for a new brand; archived units cannot be referenced.
        await owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "yeni ad" });
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/products", new { name = "X", defaultUnitId = unit.Id, taxRate = 20m, sku = "X-1" }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
    }

    [Fact]
    public async Task Category_depth_is_limited_to_four()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        Guid? parent = null;
        for (var depth = 1; depth <= 4; depth++)
        {
            parent = (await owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = $"Seviye {depth}", parentId = parent })).Id;
        }

        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/categories", new { name = "Seviye 5", parentId = parent }), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task Sku_and_barcode_are_unique_per_tenant_but_reusable_in_another_tenant()
    {
        var tenantA = await CatalogClient.NewTenantOwnerAsync(fixture);
        var tenantB = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unitA = await tenantA.CreateUnitAsync();
        var unitB = await tenantB.CreateUnitAsync();

        var first = await tenantA.CreateSimpleProductAsync(unitA.Id, "Ön balata", "FRN-001", "8690000000101");

        // Same SKU (case-insensitive) / same barcode in the same tenant.
        await HttpAssert.ProblemAsync(
            await tenantA.PostAsync("/api/v1/products", new { name = "Kopya", defaultUnitId = unitA.Id, taxRate = 20m, sku = "frn-001" }),
            HttpStatusCode.Conflict, "catalog.sku_taken");
        await HttpAssert.ProblemAsync(
            await tenantA.PostAsync("/api/v1/products", new { name = "Kopya", defaultUnitId = unitA.Id, taxRate = 20m, sku = "FRN-002", barcode = "8690000000101" }),
            HttpStatusCode.Conflict, "catalog.barcode_taken");
        await HttpAssert.ProblemAsync(
            await tenantA.PostAsync($"/api/v1/products/{first.Id}/variants", new { sku = "FRN-001" }), HttpStatusCode.Conflict, "catalog.sku_taken");
        await HttpAssert.ProblemAsync(
            await tenantA.PostAsync("/api/v1/products", new
            {
                name = "Çift",
                defaultUnitId = unitA.Id,
                taxRate = 20m,
                variants = new[] { new { sku = "D-1" }, new { sku = "d-1" } },
            }),
            HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Products without a barcode do not collide on NULL.
        await tenantA.CreateSimpleProductAsync(unitA.Id, "Barkodsuz 1", "NB-1");
        await tenantA.CreateSimpleProductAsync(unitA.Id, "Barkodsuz 2", "NB-2");

        // Another tenant may use the same SKU and barcode.
        var other = await tenantB.CreateSimpleProductAsync(unitB.Id, "Ön balata", "FRN-001", "8690000000101");
        Assert.Equal("FRN-001", Assert.Single(other.Variants).Sku);

        // SKU stays reserved after archiving (domain-model §3).
        var archivable = await tenantA.CreateSimpleProductAsync(unitA.Id, "Arşivlenecek", "ARC-1");
        Assert.Equal(HttpStatusCode.NoContent, (await tenantA.DeleteAsync($"/api/v1/products/{archivable.Id}", "W/\"1\"")).StatusCode);
        await HttpAssert.ProblemAsync(
            await tenantA.PostAsync("/api/v1/products", new { name = "Tekrar", defaultUnitId = unitA.Id, taxRate = 20m, sku = "ARC-1" }),
            HttpStatusCode.Conflict, "catalog.sku_taken");

        // Duplicate unit code / brand name / sibling category name.
        await HttpAssert.ProblemAsync(await tenantA.PostAsync("/api/v1/units", new { code = "ADET", name = "Adet 2" }), HttpStatusCode.Conflict, "catalog.unit_code_taken");
        await tenantA.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Marka" });
        await HttpAssert.ProblemAsync(await tenantA.PostAsync("/api/v1/brands", new { name = "MARKA" }), HttpStatusCode.Conflict, "catalog.brand_name_taken");
        await tenantA.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Fren" });
        await HttpAssert.ProblemAsync(await tenantA.PostAsync("/api/v1/categories", new { name = "fren" }), HttpStatusCode.Conflict, "catalog.category_name_taken");
    }

    [Fact]
    public async Task Tenant_A_cannot_read_or_write_tenant_B_catalog_through_the_api()
    {
        var tenantA = await CatalogClient.NewTenantOwnerAsync(fixture);
        var tenantB = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unitB = await tenantB.CreateUnitAsync();
        var brandB = await tenantB.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "B Marka" });
        var productB = await tenantB.CreateSimpleProductAsync(unitB.Id, "B ürünü", "B-SKU-1", "8690000000200");
        var variantB = productB.Variants[0];

        // A's user selecting tenant B: no membership.
        var spoofed = tenantA.Targeting(tenantB.TenantId);
        await HttpAssert.ProblemAsync(await spoofed.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(await spoofed.GetAsync($"/api/v1/products/{productB.Id}"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(await spoofed.PatchAsync($"/api/v1/products/{productB.Id}", new { name = "hacked" }, "W/\"1\""), HttpStatusCode.Forbidden, "tenant.access_denied");

        // A's user in its own tenant addressing B's ids: invisible.
        await HttpAssert.ProblemAsync(await tenantA.GetAsync($"/api/v1/products/{productB.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.GetAsync($"/api/v1/variants/{variantB.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.GetAsync("/api/v1/variants/lookup?barcode=8690000000200"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.PatchAsync($"/api/v1/products/{productB.Id}", new { name = "hacked" }, "W/\"1\""), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.PatchAsync($"/api/v1/variants/{variantB.Id}", new { sku = "HACK" }, "W/\"1\""), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.DeleteAsync($"/api/v1/products/{productB.Id}", "W/\"1\""), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.DeleteAsync($"/api/v1/brands/{brandB.Id}", "W/\"1\""), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await tenantA.PostAsync($"/api/v1/products/{productB.Id}/variants", new { sku = "HACK-2" }), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Empty((await tenantA.OkAsync<PagedResponse<ProductResponse>>(await tenantA.GetAsync("/api/v1/products?q=B-SKU"))).Items);

        // A cannot attach B's unit/brand to its own product.
        await HttpAssert.ProblemAsync(
            await tenantA.PostAsync("/api/v1/products", new { name = "A ürünü", defaultUnitId = unitB.Id, brandId = brandB.Id, taxRate = 20m, sku = "A-1" }),
            HttpStatusCode.UnprocessableEntity, "validation.failed");

        var stillB = await tenantB.OkAsync<ProductResponse>(await tenantB.GetAsync($"/api/v1/products/{productB.Id}"));
        Assert.Equal(("B ürünü", 1L, "active"), (stillB.Name, stillB.Version, stillB.Status));
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("warehouse_staff")]
    [InlineData("viewer")]
    public async Task Roles_without_catalog_write_can_read_but_not_write(string role)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var brand = await owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Marka" });
        var product = await owner.CreateSimpleProductAsync(unit.Id, "Buji", "ELK-BUJI-01");
        var member = await owner.AddMemberAsync(fixture, role);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/v1/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/units")).StatusCode);

        await HttpAssert.ProblemAsync(
            await member.PostAsync("/api/v1/products", new { name = "X", defaultUnitId = unit.Id, taxRate = 20m, sku = "X-1" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PatchAsync($"/api/v1/products/{product.Id}", new { name = "Y" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.DeleteAsync($"/api/v1/products/{product.Id}", "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PostAsync($"/api/v1/products/{product.Id}/variants", new { sku = "Z-1" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PatchAsync($"/api/v1/variants/{product.Variants[0].Id}", new { barcode = "123" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/brands", new { name = "B" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.DeleteAsync($"/api/v1/brands/{brand.Id}", "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/units", new { code = "kg", name = "Kilogram" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/categories", new { name = "K" }), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Fact]
    public async Task Manager_writes_products_but_units_and_categories_need_catalog_fields_manage()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var manager = await owner.AddMemberAsync(fixture, "manager");
        var admin = await owner.AddMemberAsync(fixture, "admin");

        await manager.CreateSimpleProductAsync(unit.Id, "Zincir dişli seti", "AKT-NMAX155-ZDS");
        await manager.CreatedAsync<BrandResponse>("/api/v1/brands", new { name = "Yönetici markası" });
        await HttpAssert.ProblemAsync(await manager.PostAsync("/api/v1/units", new { code = "kutu", name = "Kutu" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await manager.PostAsync("/api/v1/categories", new { name = "Aktarma" }), HttpStatusCode.Forbidden, "auth.forbidden");

        await admin.CreateUnitAsync("kutu", "Kutu");
        await admin.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name = "Aktarma" });
    }

    [Fact]
    public async Task If_match_is_required_and_a_stale_version_returns_412()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var product = await owner.CreateSimpleProductAsync(unit.Id, "Fren hidroliği", "HYD-01");
        var variant = product.Variants[0];

        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { name = "A" }, ifMatch: null), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/products/{product.Id}", ifMatch: null), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { name = "A" }, "W/\"9\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { name = "A" }, "garbage"), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");

        // Two editors read version 1; the first wins, the second gets 412 and nothing is overwritten.
        var first = await owner.PatchAsync($"/api/v1/products/{product.Id}", new { name = "Editör 1" }, "W/\"1\"");
        Assert.Equal("W/\"2\"", first.Headers.ETag?.ToString());
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { name = "Editör 2" }, "W/\"1\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/products/{product.Id}", "W/\"1\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        Assert.Equal("Editör 1", (await owner.OkAsync<ProductResponse>(await owner.GetAsync($"/api/v1/products/{product.Id}"))).Name);

        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/variants/{variant.Id}", new { name = "v" }, "W/\"5\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/units/{unit.Id}", new { name = "u" }, "W/\"5\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        var variantEtag = (await owner.GetAsync($"/api/v1/variants/{variant.Id}")).Headers.ETag?.ToString();
        Assert.Equal("W/\"1\"", variantEtag);
    }

    [Fact]
    public async Task Validation_and_malformed_requests_use_problem_codes()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var product = await owner.CreateSimpleProductAsync(unit.Id, "Ürün", "VAL-1");

        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/products", new { name = "", taxRate = 150m }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/products", new { name = "Ürün", defaultUnitId = unit.Id, taxRate = 20m, sku = "A", variants = new[] { new { sku = "B" } } }),
            HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { status = "archived" }, "W/\"1\""), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", new { taxRate = "yirmi" }, "W/\"1\""), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await owner.PatchAsync($"/api/v1/products/{product.Id}", "[1,2]", "W/\"1\""), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?colour=red"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?sort=price"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?limit=500"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?cursor=not-a-cursor"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?status=deleted"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products?categoryId=abc"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync($"/api/v1/products/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "resource.not_found");
    }

    [Fact]
    public async Task Keyset_pagination_is_stable_under_concurrent_inserts_and_equal_sort_keys()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            // Identical names: the name sort must tie-break on id.
            created.Add((await owner.CreateSimpleProductAsync(unit.Id, "Aynı ad", $"PG-{i:00}")).Id);
        }

        foreach (var sort in new[] { "-createdAt", "name" })
        {
            var seen = new List<Guid>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var url = $"/api/v1/products?sort={sort}&limit=3" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
                var page = await owner.OkAsync<PagedResponse<ProductResponse>>(await owner.GetAsync(url));
                Assert.Equal(3, page.Page.Limit);
                Assert.True(page.Items.Count <= 3);
                seen.AddRange(page.Items.Select(p => p.Id));
                cursor = page.Page.NextCursor;
                Assert.Equal(cursor is not null, page.Page.HasMore);

                if (pages++ == 0)
                {
                    // A product created after the first page must neither duplicate nor shift later pages.
                    created.Add((await owner.CreateSimpleProductAsync(unit.Id, "Aynı ad", $"PG-NEW-{sort}")).Id);
                }
            }
            while (cursor is not null);

            Assert.Equal(seen.Count, seen.Distinct().Count());
            var expectedBeforeInsert = created.Take(created.Count - 1).ToHashSet();
            Assert.Subset(seen.ToHashSet(), expectedBeforeInsert);
            if (sort == "-createdAt")
            {
                Assert.Equal(created.Take(created.Count - 1).Reverse(), seen);   // UUIDv7 + created_at order, newest first
            }
            else
            {
                Assert.Equal(seen.Order(), seen);                                // same name → ascending id
            }
        }

        // A cursor issued for one sort is rejected for another.
        var first = await owner.OkAsync<PagedResponse<ProductResponse>>(await owner.GetAsync("/api/v1/products?sort=name&limit=1"));
        await HttpAssert.ProblemAsync(
            await owner.GetAsync("/api/v1/products?limit=1&cursor=" + Uri.EscapeDataString(first.Page.NextCursor!)), HttpStatusCode.BadRequest, "request.malformed");

        // Variant list pages by SKU.
        var skus = new List<string>();
        string? variantCursor = null;
        do
        {
            var page = await owner.OkAsync<PagedResponse<VariantResponse>>(await owner.GetAsync(
                "/api/v1/variants?limit=4" + (variantCursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(variantCursor))));
            skus.AddRange(page.Items.Select(v => v.Sku));
            variantCursor = page.Page.NextCursor;
        }
        while (variantCursor is not null);

        Assert.Equal(9, skus.Count);
        Assert.Equal(skus.Order(StringComparer.Ordinal), skus);
    }

    [Fact]
    public async Task Create_product_is_idempotent_per_tenant_key()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var body = new { name = "Buji NS200 (kutu)", defaultUnitId = unit.Id, taxRate = 20m, sku = "ELK-BUJI-NS200", barcode = "8690000000309" };
        var key = Guid.NewGuid().ToString();

        var first = await owner.PostAsync("/api/v1/products", body, key);
        var replay = await owner.PostAsync("/api/v1/products", body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
        Assert.Single((await owner.OkAsync<PagedResponse<ProductResponse>>(await owner.GetAsync("/api/v1/products?q=ELK-BUJI"))).Items);

        // A different body under the same key is rejected; a missing key is 428.
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/products", new { name = "Başka", defaultUnitId = unit.Id, taxRate = 20m, sku = "OTHER-1" }, key),
            HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        using var withoutKey = new HttpRequestMessage(HttpMethod.Post, "/api/v1/brands") { Content = JsonContent.Create(new { name = "Anahtarsız" }) };
        withoutKey.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
        await HttpAssert.ProblemAsync(
            await fixture.CreateClientFor(owner.UserId).SendAsync(withoutKey, Ct), HttpStatusCode.PreconditionRequired, "precondition.required");

        // The key is tenant-scoped: the same key in another tenant creates that tenant's own product.
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        var otherUnit = await other.CreateUnitAsync();
        var otherCreate = await other.PostAsync(
            "/api/v1/products", new { name = "Buji NS200 (kutu)", defaultUnitId = otherUnit.Id, taxRate = 20m, sku = "ELK-BUJI-NS200" }, key);
        Assert.Equal(HttpStatusCode.Created, otherCreate.StatusCode);
        Assert.False(otherCreate.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(first.Headers.Location, otherCreate.Headers.Location);
    }
}
