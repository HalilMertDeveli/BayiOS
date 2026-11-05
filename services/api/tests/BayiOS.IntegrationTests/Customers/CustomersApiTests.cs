using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Pricing;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Customers;

/// <summary>Customers, addresses and dealer groups (#31): CRUD, search, paging, permissions, personal data, isolation.</summary>
public class CustomersApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Customer_crud_with_merge_patch_etags_and_validation()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var owner = kit.Owner;
        var list = await kit.PriceListAsync("Bayi A");
        var group = await kit.GroupAsync("A Grubu", list.Id, 5m);

        var created = await owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new
        {
            code = " ist-001 ",
            name = "Sentetik Motor Yedek Parça Ltd.",
            contactName = "Test Yetkili",
            taxNumber = "1234567890",
            taxOffice = "Kadıköy",
            phone = "+90 216 000 00 00",
            email = "satin.alma@example.test",
            whatsappNumbers = new List<string> { "+90 532 000 00 01", "+905320000001" },
            dealerGroupId = group.Id,
            paymentTermDays = 30,
            creditLimit = 150000.1234m,
            currency = "try",
        });
        var c = created.Customer;
        Assert.Equal(("IST-001", "TRY", 1L, "active"), (c.Code, c.Currency, c.Version, c.Status));
        Assert.Equal(["+905320000001"], c.WhatsappNumbers);
        Assert.Equal((150000.1234m, group.Id), (c.CreditLimit!.Value, c.DealerGroupId!.Value));

        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/customers", new { code = "IST-001", name = "Kopya" }), HttpStatusCode.Conflict, "customers.code_taken");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/customers", new { code = "X 1", name = "", taxNumber = "12", email = "nope", whatsappNumbers = new List<string> { "0532" }, creditLimit = -1m }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/customers", new { code = "X2", name = "X", dealerGroupId = Guid.NewGuid() }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Merge patch: absent unchanged, null clears; code/name cannot be cleared.
        var patched = await owner.OkAsync<CustomerDetailResponse>(await owner.PatchAsync(
            $"/api/v1/customers/{c.Id}", new { email = (string?)null, priceListId = list.Id, notes = "Pazartesi teslimat" }, "W/\"1\""));
        Assert.Equal(((string?)null, list.Id, "Pazartesi teslimat", "+90 216 000 00 00", 2L),
            (patched.Customer.Email, patched.Customer.PriceListId!.Value, patched.Customer.Notes, patched.Customer.Phone, patched.Customer.Version));
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/customers/{c.Id}", new { name = (string?)null }, "W/\"2\""), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/customers/{c.Id}", new { notes = "x" }, "W/\"1\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/customers/{c.Id}", new { notes = "x" }, null), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/customers/{c.Id}", "[1]", "W/\"2\""), HttpStatusCode.BadRequest, "request.malformed");

        var get = await owner.GetAsync($"/api/v1/customers/{c.Id}");
        Assert.Equal("W/\"2\"", get.Headers.ETag?.ToString());
        Assert.Equal("1234567890", (await owner.OkAsync<CustomerDetailResponse>(get)).Customer.TaxNumber);

        // Archive via status; archived customers are hidden from the default list and their addresses are frozen.
        var archived = await owner.OkAsync<CustomerDetailResponse>(await owner.PatchAsync($"/api/v1/customers/{c.Id}", new { status = "archived" }, "W/\"2\""));
        Assert.Equal("archived", archived.Customer.Status);
        Assert.Empty((await owner.OkAsync<CustomerPageResponse>(await owner.GetAsync("/api/v1/customers"))).Items);
        Assert.Single((await owner.OkAsync<CustomerPageResponse>(await owner.GetAsync("/api/v1/customers?status=archived"))).Items);
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/customers/{c.Id}/addresses", new { line1 = "X", city = "İstanbul" }), HttpStatusCode.Conflict, "customers.archived");
    }

    [Fact]
    public async Task Addresses_keep_one_default_per_type()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var owner = kit.Owner;
        var customer = await kit.CustomerAsync();
        var path = $"/api/v1/customers/{customer.Id}/addresses";

        var shipping1 = await owner.CreatedAsync<AddressResponse>(path, new { title = "Merkez", line1 = "Sanayi Sitesi 1. Blok No: 1", district = "Ümraniye", city = "İstanbul" });
        Assert.Equal(("shipping", true, "TR"), (shipping1.Type, shipping1.IsDefault, shipping1.Country));
        var shipping2 = await owner.CreatedAsync<AddressResponse>(path, new { line1 = "Depo Cad. No: 2", city = "Kocaeli", country = "tr", isDefault = false });
        Assert.False(shipping2.IsDefault);
        var billing = await owner.CreatedAsync<AddressResponse>(path, new { type = "billing", line1 = "Fatura Sok. 3", city = "İstanbul" });
        Assert.True(billing.IsDefault);
        var shipping3 = await owner.CreatedAsync<AddressResponse>(path, new { line1 = "Yeni Depo 4", city = "Bursa", isDefault = true });
        Assert.True(shipping3.IsDefault);

        var addresses = (await owner.OkAsync<AddressListResponse>(await owner.GetAsync(path))).Items;
        Assert.Equal([billing.Id], addresses.Where(a => a.Type == "billing" && a.IsDefault).Select(a => a.Id));
        Assert.Equal([shipping3.Id], addresses.Where(a => a.Type == "shipping" && a.IsDefault).Select(a => a.Id));

        var moved = await owner.OkAsync<AddressResponse>(await owner.PatchAsync($"{path}/{shipping2.Id}", new { isDefault = true, postalCode = "41000" }, "W/\"1\""));
        Assert.Equal((true, "41000"), (moved.IsDefault, moved.PostalCode));
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"{path}/{shipping2.Id}", new { type = "billing" }, "W/\"2\""), HttpStatusCode.Conflict, "customers.default_address");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync(path, new { type = "home", line1 = "", city = "" }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Deleting the default promotes the oldest remaining address of that type.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{path}/{shipping2.Id}", null)).StatusCode);
        var after = (await owner.OkAsync<CustomerDetailResponse>(await owner.GetAsync($"/api/v1/customers/{customer.Id}"))).Addresses;
        Assert.Equal([shipping1.Id], after.Where(a => a.Type == "shipping" && a.IsDefault).Select(a => a.Id));
        Assert.Equal(3, after.Count);
    }

    [Fact]
    public async Task Search_filters_and_keyset_pagination()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var owner = kit.Owner;
        var group = await kit.GroupAsync("LED Bayileri");
        foreach (var (code, name) in new[] { ("L1", "Aydınlatma A.Ş."), ("L2", "Bora LED"), ("L3", "Ceren Elektrik"), ("M1", "Deniz Motor"), ("M2", "Ege Moto") })
        {
            await owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new
            {
                code,
                name,
                taxNumber = code == "L3" ? "12345678901" : null,
                whatsappNumbers = code == "M2" ? new List<string> { "+905551112233" } : null,
                dealerGroupId = code.StartsWith('L') ? group.Id : (Guid?)null,
            });
        }

        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = await owner.OkAsync<CustomerPageResponse>(await owner.GetAsync("/api/v1/customers?limit=2" + (cursor is null ? string.Empty : "&cursor=" + cursor)));
            Assert.True(page.Items.Count <= 2);
            names.AddRange(page.Items.Select(c => c.Name));
            cursor = page.Page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(["Aydınlatma A.Ş.", "Bora LED", "Ceren Elektrik", "Deniz Motor", "Ege Moto"], names);

        async Task<IEnumerable<string>> Codes(string query) =>
            (await owner.OkAsync<CustomerPageResponse>(await owner.GetAsync("/api/v1/customers?" + query))).Items.Select(c => c.Code);
        Assert.Equal(["L2"], await Codes("q=led"));
        Assert.Equal(["M1", "M2"], await Codes("q=mot"));
        Assert.Equal(["L3"], await Codes("q=12345678901"));
        Assert.Equal(["M2"], await Codes("q=%2B90%20555%20111%2022%2033"));
        Assert.Equal(["L1", "L2", "L3"], await Codes($"dealerGroupId={group.Id}"));
        Assert.Equal(["M1"], await Codes("q=m1"));

        // TCKN-length tax numbers are masked in lists, shown in full on the detail.
        var listed = (await owner.OkAsync<CustomerPageResponse>(await owner.GetAsync("/api/v1/customers?q=Ceren"))).Items.Single();
        Assert.Equal("*******8901", listed.TaxNumber);

        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/customers?cursor=bozuk"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/customers?limit=0"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/customers?status=gone"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/customers?sort=name"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Dealer_groups_have_case_insensitive_unique_names_and_can_be_archived()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var owner = kit.Owner;
        var list = await kit.PriceListAsync("Bayi");
        var group = await kit.GroupAsync("Bayi Grubu", list.Id, 7.5m);
        Assert.Equal((list.Id, 7.5m, "active"), (group.PriceListId!.Value, group.DefaultDiscountPercent!.Value, group.Status));

        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/dealer-groups", new { name = "BAYI GRUBU" }), HttpStatusCode.Conflict, "customers.group_name_taken");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/dealer-groups", new { name = "X", defaultDiscountPercent = 120m }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        var archived = await owner.OkAsync<DealerGroupResponse>(await owner.PatchAsync(
            $"/api/v1/dealer-groups/{group.Id}", new { status = "archived", priceListId = (Guid?)null }, "W/\"1\""));
        Assert.Equal(("archived", (Guid?)null), (archived.Status, archived.PriceListId));
        Assert.Empty((await owner.OkAsync<DealerGroupListResponse>(await owner.GetAsync("/api/v1/dealer-groups"))).Items);
        Assert.Single((await owner.OkAsync<DealerGroupListResponse>(await owner.GetAsync("/api/v1/dealer-groups?includeArchived=true"))).Items);
        // The name is free again, and an archived group cannot be assigned.
        await kit.GroupAsync("bayi grubu");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/customers", new { code = "Z1", name = "Z", dealerGroupId = group.Id }), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task Sales_edits_contacts_but_needs_pricelists_assign_for_price_conditions()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var list = await kit.PriceListAsync("Bayi");
        var group = await kit.GroupAsync("Grup");
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);

        var created = await sales.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "S1", name = "Satışçının Müşterisi" });
        await HttpAssert.ProblemAsync(
            await sales.PostAsync("/api/v1/customers", new { code = "S2", name = "X", dealerGroupId = group.Id }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await sales.PatchAsync($"/api/v1/customers/{created.Customer.Id}", new { priceListId = list.Id }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        var edited = await sales.OkAsync<CustomerDetailResponse>(
            await sales.PatchAsync($"/api/v1/customers/{created.Customer.Id}", new { phone = "0216 000 00 01", dealerGroupId = (Guid?)null }, "W/\"1\""));
        Assert.Equal("0216 000 00 01", edited.Customer.Phone);
        await HttpAssert.ProblemAsync(await sales.PostAsync("/api/v1/dealer-groups", new { name = "G2" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await sales.PostAsync($"/api/v1/customers/{created.Customer.Id}/anonymize", new { }), HttpStatusCode.Forbidden, "auth.forbidden");

        // Manager: manages groups and assigns lists, but erasure stays with owner/admin.
        var manager = await kit.Owner.AddMemberAsync(fixture, Roles.Manager);
        var assigned = await manager.OkAsync<CustomerDetailResponse>(
            await manager.PatchAsync($"/api/v1/customers/{created.Customer.Id}", new { dealerGroupId = group.Id }, "W/\"2\""));
        Assert.Equal(group.Id, assigned.Customer.DealerGroupId);
        await manager.CreatedAsync<DealerGroupResponse>("/api/v1/dealer-groups", new { name = "Yönetici Grubu", priceListId = list.Id });
        await HttpAssert.ProblemAsync(
            await manager.PostAsync($"/api/v1/customers/{created.Customer.Id}/anonymize", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Theory]
    [InlineData(Roles.Viewer, true)]
    [InlineData(Roles.WarehouseStaff, false)]
    public async Task Read_only_roles_cannot_write_customers(string role, bool canRead)
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var customer = await kit.CustomerAsync();
        var member = await kit.Owner.AddMemberAsync(fixture, role);

        var read = await member.GetAsync($"/api/v1/customers/{customer.Id}");
        if (canRead)
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        else
        {
            await HttpAssert.ProblemAsync(read, HttpStatusCode.Forbidden, "auth.forbidden");
        }

        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/customers", new { code = "V1", name = "V" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await member.PatchAsync($"/api/v1/customers/{customer.Id}", new { name = "V" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await member.PostAsync($"/api/v1/customers/{customer.Id}/addresses", new { line1 = "X", city = "Y" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/dealer-groups", new { name = "G" }), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Fact]
    public async Task Anonymize_erases_personal_data_and_keeps_the_record()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var owner = kit.Owner;
        var created = await owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new
        {
            code = "SAHIS-1",
            name = "Sentetik Şahıs Firması",
            contactName = "Örnek Kişi",
            taxNumber = "10000000146",
            phone = "+90 532 000 00 02",
            email = "kisi@example.test",
            whatsappNumbers = new List<string> { "+905320000002" },
            notes = "Kişisel not",
        });
        await owner.CreatedAsync<AddressResponse>($"/api/v1/customers/{created.Customer.Id}/addresses", new { line1 = "Ev adresi 1", city = "İzmir" });

        var anonymized = (await owner.OkAsync<CustomerDetailResponse>(await owner.PostAsync($"/api/v1/customers/{created.Customer.Id}/anonymize", new { }))).Customer;

        Assert.StartsWith("Silinmiş müşteri #", anonymized.Name, StringComparison.Ordinal);
        Assert.Equal(("SAHIS-1", "archived"), (anonymized.Code, anonymized.Status));
        Assert.All(
            new[] { anonymized.ContactName, anonymized.TaxNumber, anonymized.Phone, anonymized.Email, anonymized.Notes },
            Assert.Null);
        Assert.Empty(anonymized.WhatsappNumbers);
        Assert.NotNull(anonymized.AnonymizedAt);
        Assert.Empty((await owner.OkAsync<AddressListResponse>(await owner.GetAsync($"/api/v1/customers/{created.Customer.Id}/addresses"))).Items);
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/customers/{created.Customer.Id}", new { phone = "+90 532 000 00 03" }, $"W/\"{anonymized.Version}\""),
            HttpStatusCode.Conflict,
            "customers.anonymized");

        // Idempotent (#115): a repeat returns the anonymized customer unchanged (same version, same erasure time).
        var again = (await owner.OkAsync<CustomerDetailResponse>(await owner.PostAsync($"/api/v1/customers/{created.Customer.Id}/anonymize", new { }))).Customer;
        Assert.Equal((anonymized.Name, anonymized.Version, anonymized.AnonymizedAt), (again.Name, again.Version, again.AnonymizedAt));
    }

    [Fact]
    public async Task Customer_create_is_idempotent()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var key = Guid.NewGuid().ToString();
        var body = new { code = "IDEM-1", name = "Tekrar Deneme Ltd." };

        var first = await kit.Owner.PostAsync("/api/v1/customers", body, key);
        var replay = await kit.Owner.PostAsync("/api/v1/customers", body, key);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, replay.StatusCode));
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<CustomerDetailResponse>(Ct))!.Customer.Id,
            (await replay.Content.ReadFromJsonAsync<CustomerDetailResponse>(Ct))!.Customer.Id);
        Assert.Single((await kit.Owner.OkAsync<CustomerPageResponse>(await kit.Owner.GetAsync("/api/v1/customers"))).Items);
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Post, "/api/v1/dealer-groups", new { name = "G" }, idempotencyKey: key),
            HttpStatusCode.UnprocessableEntity,
            "idempotency.key_reused");
    }

    [Fact]
    public async Task Another_tenants_customers_and_groups_are_invisible()
    {
        var a = await PricingKit.CreateAsync(fixture);
        var b = await PricingKit.CreateAsync(fixture);
        var groupA = await a.GroupAsync("A Grubu");
        var customerA = await a.CustomerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/customers/{customerA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/customers/{customerA.Id}/addresses")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PatchAsync($"/api/v1/customers/{customerA.Id}", new { name = "X" }, "W/\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/customers/{customerA.Id}/addresses", new { line1 = "X", city = "Y" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/dealer-groups/{groupA.Id}")).StatusCode);
        Assert.Empty((await b.Owner.OkAsync<CustomerPageResponse>(await b.Owner.GetAsync("/api/v1/customers"))).Items);
        await HttpAssert.ProblemAsync(
            await b.Owner.PostAsync("/api/v1/customers", new { code = "B1", name = "B", dealerGroupId = groupA.Id }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await b.Owner.Targeting(a.TenantId).GetAsync("/api/v1/customers"), HttpStatusCode.Forbidden, "tenant.access_denied");
        // Same code in another tenant is fine.
        await b.CustomerAsync(customerA.Code);
    }
}
