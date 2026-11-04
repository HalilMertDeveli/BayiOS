using System.Net;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Contracts;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>Business profile (PATCH /tenant) and VAT defaults in tenant settings (#14; PRD TEN-03/04/05).</summary>
public class BusinessSettingsApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Owner_updates_business_profile_with_etag()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "general");

        var get = await owner.GetAsync("/api/v1/tenant");
        Assert.Equal("W/\"1\"", get.Headers.ETag?.ToString());
        var tenant = await owner.OkAsync<TenantResponse>(get);
        Assert.Equal(("TRY", (string?)null, 1L), (tenant.DefaultCurrency, tenant.LogoObjectPath, tenant.Version));

        var logo = $"{owner.TenantId}/logo/bayi-logo.png";
        var body = new { sector = "led_electronics", defaultCurrency = "eur", logoObjectPath = logo, taxNumber = "1234567890", taxOffice = "Sentetik VD", phone = "+90 555 000 00 00" };
        await HttpAssert.ProblemAsync(await owner.PatchAsync("/api/v1/tenant", body, ifMatch: null), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(await owner.PatchAsync("/api/v1/tenant", body, "W/\"5\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");

        var response = await owner.PatchAsync("/api/v1/tenant", body, "W/\"1\"");
        Assert.Equal("W/\"2\"", response.Headers.ETag?.ToString());
        var updated = await owner.OkAsync<TenantResponse>(response);
        Assert.Equal(("led_electronics", "EUR", logo, "1234567890", "Sentetik VD", "+90 555 000 00 00", 2L), (updated.Sector, updated.DefaultCurrency, updated.LogoObjectPath, updated.TaxNumber, updated.TaxOffice, updated.Phone, updated.Version));
        Assert.Equal(tenant.Name, updated.Name);

        // null clears optional fields; omitted fields stay.
        var cleared = await owner.OkAsync<TenantResponse>(await owner.PatchAsync(
            "/api/v1/tenant", new Dictionary<string, object?> { ["logoObjectPath"] = null, ["phone"] = null }, "W/\"2\""));
        Assert.Equal(((string?)null, (string?)null, "EUR", 3L), (cleared.LogoObjectPath, cleared.Phone, cleared.DefaultCurrency, cleared.Version));
        Assert.Equal("EUR", (await owner.OkAsync<TenantResponse>(await owner.GetAsync("/api/v1/tenant"))).DefaultCurrency);
    }

    [Fact]
    public async Task Business_profile_rejects_invalid_values_and_foreign_logo_paths()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var other = Guid.NewGuid();

        var response = await owner.PatchAsync("/api/v1/tenant", new
        {
            name = "X",
            sector = "bakery",
            defaultCurrency = "TL",
            logoObjectPath = $"{other}/logo.png",
            taxNumber = "12AB",
            phone = "call me",
        }, "W/\"1\"");

        var errors = await ValidationErrorsAsync(response);
        Assert.Equal(["defaultCurrency", "logoObjectPath", "name", "phone", "sector", "taxNumber"], errors.Keys.Order(StringComparer.Ordinal));

        foreach (var path in new[] { $"{owner.TenantId}/../{other}/logo.png", $"{owner.TenantId}/logo.exe", "logo.png", $"/{owner.TenantId}/logo.png" })
        {
            var invalid = await ValidationErrorsAsync(await owner.PatchAsync("/api/v1/tenant", new { logoObjectPath = path }, "W/\"1\""));
            Assert.Equal(["logoObjectPath"], invalid.Keys);
        }

        var wrongType = await ValidationErrorsAsync(await owner.PatchAsync("/api/v1/tenant", new { taxNumber = 1234567890 }, "W/\"1\""));
        Assert.Equal(["taxNumber"], wrongType.Keys);
    }

    [Fact]
    public async Task Vat_defaults_live_in_settings()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);

        var defaults = await owner.OkAsync<TenantSettingsResponse>(await owner.GetAsync("/api/v1/tenant/settings"));
        Assert.Equal((20m, false), (defaults.DefaultTaxRate, defaults.PricesIncludeTaxDisplay));

        var invalid = await ValidationErrorsAsync(await owner.PatchAsync("/api/v1/tenant/settings", new { defaultTaxRate = 100.5m }, "W/\"1\""));
        Assert.Equal(["defaultTaxRate"], invalid.Keys);
        var fraction = await ValidationErrorsAsync(await owner.PatchAsync("/api/v1/tenant/settings", new { defaultTaxRate = 10.125m }, "W/\"1\""));
        Assert.Equal(["defaultTaxRate"], fraction.Keys);

        var updated = await owner.OkAsync<TenantSettingsResponse>(await owner.PatchAsync(
            "/api/v1/tenant/settings", new { defaultTaxRate = 10m, pricesIncludeTaxDisplay = true }, "W/\"1\""));
        Assert.Equal((10m, true, "block"), (updated.DefaultTaxRate, updated.PricesIncludeTaxDisplay, updated.NegativeStockPolicy));
    }

    [Fact]
    public async Task Business_profile_requires_settings_permission_and_stays_in_its_tenant()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        var admin = await owner.AddMemberAsync(fixture, "admin");
        var manager = await owner.AddMemberAsync(fixture, "manager");

        await HttpAssert.ProblemAsync(await manager.PatchAsync("/api/v1/tenant", new { phone = "0212 000 00 00" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/tenant")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync("/api/v1/tenant", new { phone = "0212 000 00 00" }, "W/\"1\"")).StatusCode);

        // Owner of another tenant cannot touch this one, even with a valid-looking ETag and its own logo prefix.
        var foreign = other.Targeting(owner.TenantId);
        await HttpAssert.ProblemAsync(
            await foreign.PatchAsync("/api/v1/tenant", new { logoObjectPath = $"{owner.TenantId}/x.png" }, "W/\"2\""), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(
            await foreign.PatchAsync("/api/v1/tenant/settings", new { defaultTaxRate = 1m }, "W/\"1\""), HttpStatusCode.Forbidden, "tenant.access_denied");

        var mine = await owner.OkAsync<TenantResponse>(await owner.GetAsync("/api/v1/tenant"));
        Assert.Equal(((string?)null, "0212 000 00 00"), (mine.LogoObjectPath, mine.Phone));
        Assert.Equal(20m, (await owner.OkAsync<TenantSettingsResponse>(await owner.GetAsync("/api/v1/tenant/settings"))).DefaultTaxRate);
        Assert.Null((await other.OkAsync<TenantResponse>(await other.GetAsync("/api/v1/tenant"))).Phone);
    }

    private static async Task<Dictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
