using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.SharedKernel.Authorization;
using Npgsql;

namespace BayiOS.IntegrationTests.Orders;

/// <summary>
/// #92: tenant order settings — approve on submit (<c>require_order_approval = false</c>, inventory-and-orders §3.1
/// #1a), the order number prefix, and the shipping / billing address snapshot. Synthetic data only.
/// </summary>
public sealed class OrderSettingsTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- §3.1 #1a: approve on submit ----

    [Fact]
    public async Task By_default_submit_waits_for_approval()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);

        var submitted = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 2m));

        Assert.Equal("pending_approval", submitted.Order.Status);
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        Assert.Equal((5m, 0m), await kit.BalanceAsync(variant));
    }

    [Fact]
    public async Task Without_required_approval_submit_approves_and_reserves_in_one_step()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        await SetSettingsAsync(kit.Owner, new { requireOrderApproval = false });

        var order = await kit.CreateOrderAsync(OrdersKit.Line(variant, 2m));
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, order, "submit"));

        Assert.Equal("approved", approved.Order.Status);
        Assert.Equal(kit.Owner.UserId, approved.Order.ApprovedBy);
        Assert.NotNull(approved.Order.SubmittedAt);
        Assert.Equal(2m, approved.Lines[0].ReservedQuantity);
        Assert.Equal(["create", "submit", "approve"], approved.History.Select(h => h.Action));
        Assert.Equal((5m, 2m), await kit.BalanceAsync(variant));
        Assert.Equal(1L, await kit.ActiveReservationsAsync());

        // The same side effects as approve: picking task, audit and outbox in the same transaction.
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Equal(approved.Order.Number, task.Task.OrderNumber);
        Assert.Equal(1L, await kit.CountAsync(
            $"SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'orders.approved.v1' AND payload->>'orderId' = '{approved.Order.Id}'"));
        Assert.Equal(1L, await kit.CountAsync(
            $"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'order.approved' AND entity_id = '{approved.Order.Id}'"));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Approve_on_submit_is_all_or_nothing_and_a_shortage_leaves_the_draft_untouched()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var plenty = await kit.VariantAsync(10m);
        var scarce = await kit.VariantAsync(20m);
        await kit.ReceiveAsync(plenty, 10m);
        await kit.ReceiveAsync(scarce, 1m);
        await SetSettingsAsync(kit.Owner, new { requireOrderApproval = false });
        var order = await kit.CreateOrderAsync(OrdersKit.Line(plenty, 3m), OrdersKit.Line(scarce, 2m));

        var response = await OrdersKit.ActAsync(kit.Owner, order, "submit");

        var problem = await HttpAssert.ProblemJsonAsync(response, HttpStatusCode.Conflict, "inventory.insufficient_stock");
        var shortage = Assert.Single(problem.GetProperty("shortages").EnumerateArray());
        Assert.Equal(scarce, shortage.GetProperty("variantId").GetGuid());
        var after = await kit.GetAsync(order.Order.Id);
        Assert.Equal(("draft", order.Order.Version, (DateTimeOffset?)null), (after.Order.Status, after.Order.Version, after.Order.SubmittedAt));
        Assert.Equal(["create"], after.History.Select(h => h.Action));
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        Assert.Equal(((10m, 0m), (1m, 0m)), (await kit.BalanceAsync(plenty), await kit.BalanceAsync(scarce)));
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM warehouse.picking_tasks WHERE tenant_id = $1"));

        // Once stock arrives the same draft submits and approves.
        await kit.ReceiveAsync(scarce, 1m);
        Assert.Equal("approved", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, after, "submit"))).Order.Status);
        Assert.Equal(((10m, 3m), (2m, 2m)), (await kit.BalanceAsync(plenty), await kit.BalanceAsync(scarce)));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Approve_on_submit_honours_the_warn_policy_confirmation()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 1m);
        await SetSettingsAsync(kit.Owner, new { requireOrderApproval = false, negativeStockPolicy = "warn" });
        var order = await kit.CreateOrderAsync(OrdersKit.Line(variant, 2m));

        var unconfirmed = await HttpAssert.ProblemJsonAsync(
            await OrdersKit.ActAsync(kit.Owner, order, "submit"), HttpStatusCode.Conflict, "inventory.insufficient_stock");
        Assert.True(unconfirmed.GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal("draft", (await kit.GetAsync(order.Order.Id)).Order.Status);

        var confirmed = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, order, "submit", new { confirmNegativeStock = true }));
        Assert.Equal("approved", confirmed.Order.Status);
        Assert.Equal((1m, 2m), await kit.BalanceAsync(variant));
    }

    [Fact]
    public async Task A_submitter_who_may_not_approve_falls_back_to_pending_approval()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        await SetSettingsAsync(kit.Owner, new { requireOrderApproval = false });

        // Sales may approve under the default rule (footnote 9 on) → approved on submit.
        var first = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);
        Assert.Equal("approved", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, first, "submit"))).Order.Status);

        // Rule off → sales lacks orders.approve → the order waits for a manager, nothing is reserved for it.
        await SetSettingsAsync(kit.Owner, new { salesCanApproveOrders = false });
        var second = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, second, "submit"));
        Assert.Equal("pending_approval", pending.Order.Status);
        Assert.Null(pending.Order.ApprovedBy);
        Assert.Equal((10m, 1m), await kit.BalanceAsync(variant));

        Assert.Equal("approved", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"))).Order.Status);
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Ai_drafts_always_wait_for_a_human_approval()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        await SetSettingsAsync(kit.Owner, new { requireOrderApproval = false });

        var draft = await OrdersKit.ReadAsync(
            await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 1m)], source: "whatsapp_ai"), HttpStatusCode.Created);
        Assert.Equal("pending_approval", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"))).Order.Status);
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
    }

    // ---- Order number prefix (PRD ORD-14) ----

    [Fact]
    public async Task Order_numbers_follow_the_prefix_setting_with_a_counter_per_prefix()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);

        var first = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        Assert.Matches(NumberPattern("SIP", 1), first.Order.Number);

        await SetSettingsAsync(kit.Owner, new { orderNumberPrefix = "led" });
        var led1 = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        var led2 = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        Assert.Matches(NumberPattern("LED", 1), led1.Order.Number);
        Assert.Matches(NumberPattern("LED", 2), led2.Order.Number);

        // Back to SIP: that counter resumes; existing numbers never change.
        await SetSettingsAsync(kit.Owner, new { orderNumberPrefix = "SIP" });
        Assert.Matches(NumberPattern("SIP", 2), (await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m))).Order.Number);
        Assert.Equal(led1.Order.Number, (await kit.GetAsync(led1.Order.Id)).Order.Number);

        // Another tenant with the same prefix has its own counter.
        var other = await OrdersKit.CreateAsync(fixture);
        await SetSettingsAsync(other.Owner, new { orderNumberPrefix = "LED" });
        Assert.Matches(NumberPattern("LED", 1), (await other.CreateOrderAsync(OrdersKit.Line(await other.VariantAsync(5m), 1m))).Order.Number);
    }

    [Fact]
    public async Task Parallel_creates_under_a_custom_prefix_get_unique_gapless_numbers()
    {
        const int Workers = 16;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await SetSettingsAsync(kit.Owner, new { orderNumberPrefix = "PAR1" });

        var responses = await Parallel(Workers, _ => kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 1m)]));

        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((await OrdersKit.ReadAsync(response, HttpStatusCode.Created)).Order.Number);
        }

        Assert.All(numbers, n => Assert.StartsWith("PAR1-", n, StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(1, Workers), numbers.Select(n => int.Parse(n[^6..], System.Globalization.CultureInfo.InvariantCulture)).Order());
        Assert.Equal((long)Workers, await kit.CountAsync(
            "SELECT next_value FROM orders.order_number_sequences WHERE tenant_id = $1 AND prefix = 'PAR1'"));
    }

    // ---- Address snapshot (data-model §5.8 shipping_address / billing_address) ----

    [Fact]
    public async Task Draft_shows_the_chosen_or_default_address_and_submit_snapshots_it()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var shipDefault = await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var shipOther = await AddressAsync(kit, kit.CustomerId, "shipping", "Şube", "Ankara");
        var bill = await AddressAsync(kit, kit.CustomerId, "billing", "Muhasebe", "İzmir");

        // No choice → the defaults (live preview while draft).
        var draft = await CreateWithAddressesAsync(kit, variant, null, null);
        Assert.Equal((shipDefault.Id, bill.Id), (draft.Order.ShippingAddress!.AddressId, draft.Order.BillingAddress!.AddressId));
        Assert.Equal(((Guid?)null, (Guid?)null, false), (draft.Order.ShippingAddressId, draft.Order.BillingAddressId, draft.Order.AddressesAreSnapshot));

        // Choose the other shipping address on edit.
        draft = await OrdersKit.ReadAsync(await kit.Owner.PatchAsync(
            $"/api/v1/orders/{draft.Order.Id}", new { shippingAddressId = shipOther.Id }, CatalogClient.ETag(draft.Order.Version)));
        Assert.Equal(("Ankara", shipOther.Id), (draft.Order.ShippingAddress!.City, draft.Order.ShippingAddressId));

        var submitted = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        Assert.True(submitted.Order.AddressesAreSnapshot);
        Assert.Equal(("Şube", "Ankara", "Sentetik Kişi", "+90 555 000 00 01"), (
            submitted.Order.ShippingAddress!.Title, submitted.Order.ShippingAddress.City,
            submitted.Order.ShippingAddress.ContactName, submitted.Order.ShippingAddress.ContactPhone));
        Assert.Equal("İzmir", submitted.Order.BillingAddress!.City);

        // The customer's address changes later: the submitted order keeps its snapshot.
        await PatchAddressAsync(kit, shipOther, new { city = "Bursa", title = "Yeni şube" });
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, await kit.GetAsync(draft.Order.Id), "approve"));
        Assert.Equal(("Şube", "Ankara"), (approved.Order.ShippingAddress!.Title, approved.Order.ShippingAddress.City));
        Assert.Equal("Ankara", (await kit.GetAsync(draft.Order.Id)).Order.ShippingAddress!.City);
    }

    [Fact]
    public async Task Snapshots_are_frozen_after_approval_even_for_the_table_owner()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        Assert.Equal("İstanbul", approved.Order.ShippingAddress!.City);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE orders.orders SET shipping_address = jsonb_set(shipping_address, '{city}', '\"Sahte\"') WHERE id = $1", approved.Order.Id));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE orders.orders SET billing_address_id = gen_random_uuid() WHERE id = $1", approved.Order.Id));

        // Other columns of an approved order still update (the trigger only guards the addresses).
        await ExecuteAsync("UPDATE orders.orders SET notes = notes WHERE id = $1", approved.Order.Id);
        Assert.Equal("İstanbul", (await kit.GetAsync(approved.Order.Id)).Order.ShippingAddress!.City);
    }

    [Fact]
    public async Task Withdraw_reopens_the_choice_and_resubmit_takes_a_fresh_snapshot()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        var ship = await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, await CreateWithAddressesAsync(kit, variant, null, null), "submit"));
        Assert.Equal("İstanbul", pending.Order.ShippingAddress!.City);
        Assert.Null(pending.Order.BillingAddress);

        await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "withdraw"));
        await PatchAddressAsync(kit, ship, new { city = "Kocaeli" });
        var draft = await kit.GetAsync(pending.Order.Id);
        Assert.Equal(("draft", "Kocaeli", false), (draft.Order.Status, draft.Order.ShippingAddress!.City, draft.Order.AddressesAreSnapshot));

        var resubmitted = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        Assert.Equal("Kocaeli", resubmitted.Order.ShippingAddress!.City);
    }

    [Fact]
    public async Task Address_choices_must_belong_to_the_customer_and_match_the_type()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        var ship = await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var bill = await AddressAsync(kit, kit.CustomerId, "billing", "Muhasebe", "İzmir");
        var otherCustomer = await kit.Owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "C0002", name = "Sentetik LED A.Ş." });
        var foreignShip = await AddressAsync(kit, otherCustomer.Customer.Id, "shipping", "Başka", "Adana");

        var errors = await ValidationErrorsAsync(await CreateResponseWithAddressesAsync(kit, variant, bill.Id, ship.Id));
        Assert.Equal(["billingAddressId", "shippingAddressId"], errors.Keys.Order(StringComparer.Ordinal));
        errors = await ValidationErrorsAsync(await CreateResponseWithAddressesAsync(kit, variant, foreignShip.Id, null));
        Assert.Equal(["shippingAddressId"], errors.Keys);

        // Another tenant's address id is simply unknown.
        var stranger = await OrdersKit.CreateAsync(fixture);
        var strangerShip = await AddressAsync(stranger, stranger.CustomerId, "shipping", "Yabancı", "Van");
        errors = await ValidationErrorsAsync(await CreateResponseWithAddressesAsync(kit, variant, strangerShip.Id, null));
        Assert.Equal(["shippingAddressId"], errors.Keys);

        // Changing the customer without new choices resets them to that customer's defaults.
        var draft = await CreateWithAddressesAsync(kit, variant, ship.Id, bill.Id);
        draft = await OrdersKit.ReadAsync(await kit.Owner.PatchAsync(
            $"/api/v1/orders/{draft.Order.Id}", new { customerId = otherCustomer.Customer.Id }, CatalogClient.ETag(draft.Order.Version)));
        Assert.Equal(((Guid?)null, (Guid?)null, foreignShip.Id), (draft.Order.ShippingAddressId, draft.Order.BillingAddressId, draft.Order.ShippingAddress!.AddressId));
        Assert.Null(draft.Order.BillingAddress);
    }

    [Fact]
    public async Task Submit_fails_when_the_chosen_address_was_removed()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var branch = await AddressAsync(kit, kit.CustomerId, "shipping", "Şube", "Ankara");
        var draft = await CreateWithAddressesAsync(kit, variant, branch.Id, null);

        var deleted = await kit.Owner.DeleteAsync($"/api/v1/customers/{kit.CustomerId}/addresses/{branch.Id}", CatalogClient.ETag(branch.Version));
        Assert.True(deleted.IsSuccessStatusCode, $"{(int)deleted.StatusCode} {await deleted.Content.ReadAsStringAsync(Ct)}");

        var errors = await ValidationErrorsAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        Assert.Equal(["shippingAddressId"], errors.Keys);
        Assert.Equal("draft", (await kit.GetAsync(draft.Order.Id)).Order.Status);
    }

    [Fact]
    public async Task Addresses_are_hidden_from_callers_without_customers_read()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ClaimAsync(staff, task.Task.Id)).StatusCode);

        var seen = await OrdersKit.ReadAsync(await staff.GetAsync($"/api/v1/orders/{approved.Order.Id}"));
        Assert.Null(seen.Order.ShippingAddress);
        Assert.Null(seen.Order.BillingAddress);
        Assert.NotNull((await kit.GetAsync(approved.Order.Id)).Order.ShippingAddress);
    }

    // ---- Helpers ----

    private static Regex NumberPattern(string prefix, int value) => new($"^{prefix}-[0-9]{{4}}-{value:D6}$");

    private static async Task SetSettingsAsync(CatalogClient owner, object patch)
    {
        var current = await owner.GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var response = await owner.PatchAsync("/api/v1/tenant/settings", patch, current.Headers.ETag!.ToString());
        Assert.True(response.IsSuccessStatusCode, $"settings: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static Task<AddressResponse> AddressAsync(OrdersKit kit, Guid customerId, string type, string title, string city) =>
        kit.Owner.CreatedAsync<AddressResponse>($"/api/v1/customers/{customerId}/addresses", new
        {
            type,
            title,
            line1 = "Sentetik Cad. No:1",
            district = "Merkez",
            city,
            postalCode = "34000",
            contactName = "Sentetik Kişi",
            contactPhone = "+90 555 000 00 01",
        });

    private static async Task PatchAddressAsync(OrdersKit kit, AddressResponse address, object patch)
    {
        var current = (await kit.Owner.OkAsync<AddressListResponse>(await kit.Owner.GetAsync($"/api/v1/customers/{address.CustomerId}/addresses")))
            .Items.Single(a => a.Id == address.Id);
        var response = await kit.Owner.PatchAsync(
            $"/api/v1/customers/{address.CustomerId}/addresses/{address.Id}", patch, CatalogClient.ETag(current.Version));
        Assert.True(response.IsSuccessStatusCode, $"address: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static Task<HttpResponseMessage> CreateResponseWithAddressesAsync(OrdersKit kit, Guid variant, Guid? shippingAddressId, Guid? billingAddressId) =>
        kit.Owner.PostAsync("/api/v1/orders", new
        {
            customerId = kit.CustomerId,
            warehouseId = kit.WarehouseId,
            lines = new[] { OrdersKit.Line(variant, 1m) },
            shippingAddressId,
            billingAddressId,
        });

    private static async Task<OrderDetailResponse> CreateWithAddressesAsync(OrdersKit kit, Guid variant, Guid? shippingAddressId, Guid? billingAddressId) =>
        await OrdersKit.ReadAsync(await CreateResponseWithAddressesAsync(kit, variant, shippingAddressId, billingAddressId), HttpStatusCode.Created);

    private static async Task<Dictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T[]> Parallel<T>(int workers, Func<int, Task<T>> work)
    {
        using var barrier = new Barrier(workers);
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(
            async () =>
            {
                barrier.SignalAndWait(TestContext.Current.CancellationToken);
                return await work(i);
            },
            TestContext.Current.CancellationToken));
        return await Task.WhenAll(tasks);
    }
}
