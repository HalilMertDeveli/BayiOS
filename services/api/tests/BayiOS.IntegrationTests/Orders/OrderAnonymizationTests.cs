using System.Net;
using System.Text.Json;
using BayiOS.Api.Platform.Audit;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.SharedKernel.Authorization;
using Npgsql;

namespace BayiOS.IntegrationTests.Orders;

/// <summary>
/// #115 (KVKK, data-policy §5): <c>POST /customers/{id}/anonymize</c> also masks the personal fields of every order
/// address snapshot of that customer — through the narrow exception of the post-approval immutability trigger, which
/// still rejects everything else. Synthetic data only.
/// </summary>
public sealed class OrderAnonymizationTests(ApiFixture fixture)
{
    private const string Person = "Sentetik Kişi";
    private const string Phone = "+90 555 000 00 01";
    private const string MaskedLine1 = "Silinmiş adres";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymize_masks_the_snapshots_of_every_order_of_that_customer_only()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 50m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        await AddressAsync(kit, kit.CustomerId, "billing", "Muhasebe", "İzmir");

        // One order per lifecycle stage that carries a snapshot, plus a draft that never had one.
        var withdrawn = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m)), "withdraw"));
        var pending = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var toShip = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
        var shipped = await kit.ShipAsync(toShip, (toShip.Lines[0].Id, 2m));
        var plainDraft = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        Assert.Equal(("draft", "pending_approval", "approved", "shipped"), (withdrawn.Order.Status, pending.Order.Status, approved.Order.Status, shipped.Order.Status));

        // Bystanders: another customer of the same tenant and another tenant's customer.
        var neighbour = await kit.Owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "C0002", name = "Sentetik Komşu Ltd." });
        await AddressAsync(kit, neighbour.Customer.Id, "shipping", "Komşu depo", "Ankara");
        var neighbourOrder = await ApprovedForAsync(kit, neighbour.Customer.Id, variant);
        var stranger = await OrdersKit.CreateAsync(fixture);
        var strangerVariant = await stranger.VariantAsync(10m);
        await stranger.ReceiveAsync(strangerVariant, 5m);
        await AddressAsync(stranger, stranger.CustomerId, "shipping", "Yabancı depo", "Van");
        var strangerOrder = await stranger.ApprovedOrderAsync(OrdersKit.Line(strangerVariant, 1m));

        // Tenant isolation: another tenant's owner cannot reach the customer, and nothing changes.
        await HttpAssert.ProblemAsync(
            await stranger.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Equal(4L, await PersonalSnapshotsAsync(kit, kit.CustomerId));

        var customer = (await kit.Owner.OkAsync<CustomerDetailResponse>(
            await kit.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }))).Customer;
        Assert.NotNull(customer.AnonymizedAt);

        // Order detail: snapshots masked, coarse location kept, status untouched, version bumped.
        foreach (var before in new[] { pending, approved, shipped })
        {
            var after = await kit.GetAsync(before.Order.Id);
            Assert.Equal((before.Order.Status, true, before.Order.Version + 1), (after.Order.Status, after.Order.AddressesAreSnapshot, after.Order.Version));
            AssertMasked(after.Order.ShippingAddress!, before.Order.ShippingAddress!, "İstanbul");
            AssertMasked(after.Order.BillingAddress!, before.Order.BillingAddress!, "İzmir");
        }

        // Drafts show a live preview of the customer's addresses, which are gone; the stored snapshot is masked too.
        var draftAfter = await kit.GetAsync(withdrawn.Order.Id);
        Assert.Equal(((OrderAddressResponse?)null, (OrderAddressResponse?)null), (draftAfter.Order.ShippingAddress, draftAfter.Order.BillingAddress));
        Assert.Equal(plainDraft.Order.Version, (await kit.GetAsync(plainDraft.Order.Id)).Order.Version);
        Assert.Equal(0L, await PersonalSnapshotsAsync(kit, kit.CustomerId));
        Assert.Equal(4L, await kit.CountAsync(
            $"SELECT count(*) FROM orders.orders WHERE tenant_id = $1 AND customer_id = '{kit.CustomerId}' AND shipping_address->>'line1' = '{MaskedLine1}'"));

        // Bystanders keep their snapshots.
        Assert.Equal(Person, (await kit.GetAsync(neighbourOrder.Order.Id)).Order.ShippingAddress!.ContactName);
        Assert.Equal(1L, await PersonalSnapshotsAsync(kit, neighbour.Customer.Id));
        var strangerAfter = await stranger.GetAsync(strangerOrder.Order.Id);
        Assert.Equal((Person, Phone, strangerOrder.Order.Version), (strangerAfter.Order.ShippingAddress!.ContactName, strangerAfter.Order.ShippingAddress.ContactPhone, strangerAfter.Order.Version));

        // Audit: one event, counts only, no personal data.
        var audit = Assert.Single(await AnonymizedEventsAsync(kit.Owner));
        Assert.Equal((kit.CustomerId, "customer", kit.Owner.UserId), (audit.EntityId, audit.EntityType, audit.ActorUserId));
        var changes = JsonSerializer.SerializeToElement(audit.Changes);
        Assert.Equal((4, 2, false), (changes.GetProperty("ordersMasked").GetInt32(), changes.GetProperty("addressesRemoved").GetInt32(), changes.GetProperty("repeat").GetBoolean()));
        Assert.DoesNotContain(Person, changes.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("555", changes.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(await AnonymizedEventsAsync(stranger.Owner));
    }

    [Fact]
    public async Task Re_anonymize_is_idempotent_and_masks_snapshots_written_since()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var pending = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));
        var first = (await kit.Owner.OkAsync<CustomerDetailResponse>(await kit.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }))).Customer;
        var maskedVersion = (await kit.GetAsync(approved.Order.Id)).Order.Version;

        // Repeat: 200, nothing changes, no new audit event.
        var again = (await kit.Owner.OkAsync<CustomerDetailResponse>(await kit.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }))).Customer;
        Assert.Equal((first.Version, first.AnonymizedAt), (again.Version, again.AnonymizedAt));
        Assert.Equal(maskedVersion, (await kit.GetAsync(approved.Order.Id)).Order.Version);
        Assert.Single(await AnonymizedEventsAsync(kit.Owner));

        // A snapshot holding personal data again (e.g. an order anonymized before #115; simulated on the still
        // pending order, which the trigger allows) is caught by the next call and audited as a repeat.
        await ExecuteAsync(
            $"UPDATE orders.orders SET shipping_address = shipping_address || '{{\"contactName\": \"{Person}\"}}'::jsonb WHERE id = $1", pending.Order.Id);
        Assert.Equal(1L, await PersonalSnapshotsAsync(kit, kit.CustomerId));
        await kit.Owner.OkAsync<CustomerDetailResponse>(await kit.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }));
        Assert.Equal(0L, await PersonalSnapshotsAsync(kit, kit.CustomerId));
        var events = await AnonymizedEventsAsync(kit.Owner);
        Assert.Equal(2, events.Count);
        var repeat = JsonSerializer.SerializeToElement(events.Single(e => JsonSerializer.SerializeToElement(e.Changes).GetProperty("repeat").GetBoolean()).Changes);
        Assert.Equal((1, 0), (repeat.GetProperty("ordersMasked").GetInt32(), repeat.GetProperty("addressesRemoved").GetInt32()));
    }

    [Fact]
    public async Task Anonymize_needs_the_tenant_settings_permission_and_leaves_snapshots_alone_otherwise()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));

        foreach (var role in new[] { Roles.Manager, Roles.Sales })
        {
            var member = await kit.Owner.AddMemberAsync(fixture, role);
            await HttpAssert.ProblemAsync(
                await member.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
        }

        Assert.Equal(1L, await PersonalSnapshotsAsync(kit, kit.CustomerId));
        Assert.Empty(await AnonymizedEventsAsync(kit.Owner));
    }

    [Fact]
    public async Task The_trigger_still_rejects_every_other_change_after_approval()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
        var neighbour = await kit.Owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "C0002", name = "Sentetik Komşu Ltd." });
        await AddressAsync(kit, neighbour.Customer.Id, "shipping", "Komşu depo", "Ankara");
        var order = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var neighbourOrder = await ApprovedForAsync(kit, neighbour.Customer.Id, variant);
        await kit.Owner.OkAsync<CustomerDetailResponse>(await kit.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { }));

        const string Mask = "shipping_address = orders.anonymize_address_snapshot(shipping_address)";
        var tenant = kit.TenantId;
        var own = kit.CustomerId;
        var other = neighbour.Customer.Id;
        var cases = new (string Sql, Guid OrderId, Guid? Marker, Guid? Tenant)[]
        {
            // Restoring personal data into a masked snapshot, with or without the marker.
            ($"UPDATE orders.orders SET shipping_address = shipping_address || '{{\"contactName\": \"{Person}\"}}'::jsonb WHERE id = $1", order.Order.Id, null, null),
            ($"UPDATE orders.orders SET shipping_address = shipping_address || '{{\"contactName\": \"{Person}\"}}'::jsonb WHERE id = $1", order.Order.Id, own, tenant),
            // Marker set, but a value other than the mask (a "masked" city) or an id change.
            ("UPDATE orders.orders SET shipping_address = jsonb_set(orders.anonymize_address_snapshot(shipping_address), '{city}', '\"Sahte\"') WHERE id = $1", neighbourOrder.Order.Id, other, tenant),
            ("UPDATE orders.orders SET shipping_address = jsonb_set(shipping_address, '{city}', '\"Sahte\"') WHERE id = $1", order.Order.Id, own, tenant),
            ("UPDATE orders.orders SET shipping_address_id = gen_random_uuid() WHERE id = $1", order.Order.Id, own, tenant),
            ($"UPDATE orders.orders SET {Mask}, shipping_address_id = gen_random_uuid() WHERE id = $1", neighbourOrder.Order.Id, other, tenant),
            // The exact mask, but without the marker, with another customer's marker, or under another tenant.
            ($"UPDATE orders.orders SET {Mask} WHERE id = $1", neighbourOrder.Order.Id, null, null),
            ($"UPDATE orders.orders SET {Mask} WHERE id = $1", neighbourOrder.Order.Id, own, tenant),
            ($"UPDATE orders.orders SET {Mask} WHERE id = $1", neighbourOrder.Order.Id, other, Guid.NewGuid()),
        };

        foreach (var (sql, orderId, marker, markerTenant) in cases)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, orderId, marker, markerTenant));
            Assert.True(ex.SqlState == PostgresErrorCodes.RestrictViolation, $"{sql}: {ex.SqlState} {ex.MessageText}");
        }

        // The app role (RLS, its own tenant) is bound by the same trigger.
        var appError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"UPDATE orders.orders SET {Mask} WHERE id = $1", neighbourOrder.Order.Id, null, tenant, fixture.AppConnectionString));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, appError.SqlState);

        Assert.Equal(Person, (await kit.GetAsync(neighbourOrder.Order.Id)).Order.ShippingAddress!.ContactName);
        var masked = (await kit.GetAsync(order.Order.Id)).Order.ShippingAddress!;
        Assert.Equal(("İstanbul", (string?)null, MaskedLine1), (masked.City, masked.ContactName, masked.Line1));

        // The exception itself works for the app role only with the right marker: exactly the mask, nothing more.
        await ExecuteAsync($"UPDATE orders.orders SET {Mask} WHERE id = $1", neighbourOrder.Order.Id, other, tenant, fixture.AppConnectionString);
        Assert.Null((await kit.GetAsync(neighbourOrder.Order.Id)).Order.ShippingAddress!.ContactName);
    }

    [Fact]
    public async Task Anonymize_racing_a_submit_never_leaves_an_unmasked_snapshot()
    {
        const int Rounds = 4;
        for (var round = 0; round < Rounds; round++)
        {
            var kit = await OrdersKit.CreateAsync(fixture);
            var variant = await kit.VariantAsync(10m);
            await AddressAsync(kit, kit.CustomerId, "shipping", "Merkez depo", "İstanbul");
            var drafts = new List<OrderDetailResponse>();
            for (var i = 0; i < 3; i++)
            {
                drafts.Add(await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m)));
            }

            using var barrier = new Barrier(drafts.Count + 1);
            var submits = drafts.Select(d => Task.Run(
                async () =>
                {
                    barrier.SignalAndWait(Ct);
                    return await OrdersKit.ActAsync(kit.Owner, d, "submit");
                },
                Ct)).ToList();
            var anonymize = Task.Run(
                async () =>
                {
                    barrier.SignalAndWait(Ct);
                    return await kit.Owner.PostAsync($"/api/v1/customers/{kit.CustomerId}/anonymize", new { });
                },
                Ct);

            var anonymized = await anonymize;
            Assert.True(anonymized.StatusCode == HttpStatusCode.OK, $"{(int)anonymized.StatusCode} {await anonymized.Content.ReadAsStringAsync(Ct)}");
            foreach (var submit in await Task.WhenAll(submits))
            {
                // Either it won (and was masked afterwards) or it saw the archived customer / removed address.
                Assert.True(
                    submit.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
                    $"{(int)submit.StatusCode} {await submit.Content.ReadAsStringAsync(Ct)}");
            }

            Assert.Equal(0L, await PersonalSnapshotsAsync(kit, kit.CustomerId));
        }
    }

    // ---- Helpers ----

    private static void AssertMasked(OrderAddressResponse masked, OrderAddressResponse original, string city)
    {
        Assert.Equal((original.AddressId, city, original.District, original.Country), (masked.AddressId, masked.City, masked.District, masked.Country));
        Assert.Equal(MaskedLine1, masked.Line1);
        Assert.All(new[] { masked.Title, masked.Line2, masked.PostalCode, masked.ContactName, masked.ContactPhone }, Assert.Null);
    }

    /// <summary>Orders of the customer whose stored snapshot (draft or not) still holds the synthetic contact name or phone.</summary>
    private static Task<long> PersonalSnapshotsAsync(OrdersKit kit, Guid customerId) =>
        kit.CountAsync(
            $"""
            SELECT count(*) FROM orders.orders WHERE tenant_id = $1 AND customer_id = '{customerId}'
              AND (coalesce(shipping_address::text, '') || coalesce(billing_address::text, '')) ~ '{Person}|{Phone.Replace("+", "\\+", StringComparison.Ordinal)}'
            """);

    private static async Task<List<AuditEventResponse>> AnonymizedEventsAsync(CatalogClient owner) =>
        (await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync("/api/v1/audit-events?action=customer.anonymized"))).Items.ToList();

    private static async Task<OrderDetailResponse> ApprovedForAsync(OrdersKit kit, Guid customerId, Guid variant)
    {
        var created = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 1m)], customerId: customerId), HttpStatusCode.Created);
        var submitted = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, created, "submit"));
        return await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, submitted, "approve"));
    }

    private static Task<AddressResponse> AddressAsync(OrdersKit kit, Guid customerId, string type, string title, string city) =>
        kit.Owner.CreatedAsync<AddressResponse>($"/api/v1/customers/{customerId}/addresses", new
        {
            type,
            title,
            line1 = "Sentetik Cad. No:1",
            line2 = "Daire 2",
            district = "Merkez",
            city,
            postalCode = "34000",
            contactName = Person,
            contactPhone = Phone,
        });

    /// <summary>One statement in its own transaction (superuser unless given), optionally with the anonymization marker set.</summary>
    private async Task ExecuteAsync(string sql, Guid id, Guid? marker = null, Guid? tenant = null, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var settings = new NpgsqlCommand(
            "SELECT set_config('app.anonymize_customer_id', $1, true), set_config('app.tenant_id', $2, true)", connection, transaction))
        {
            settings.Parameters.Add(new NpgsqlParameter { Value = marker?.ToString() ?? string.Empty });
            settings.Parameters.Add(new NpgsqlParameter { Value = tenant?.ToString() ?? string.Empty });
            await settings.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var rows = await command.ExecuteNonQueryAsync(Ct);
        Assert.Equal(1, rows);
        await transaction.CommitAsync(Ct);
    }
}
