using System.Collections.Concurrent;
using System.Net;
using BayiOS.Api.Platform;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace BayiOS.IntegrationTests.Contracts;

/// <summary>
/// #86: display names on order summaries/details (customer, warehouse, creator, approver, history actor) and on picking
/// task summaries (assignee), resolved in one batch per response through the module ports. warehouse_staff still sees
/// no customer data (roles-permissions §3.5: no <c>customers.read</c>) and no prices (footnote 8). Synthetic data only.
/// </summary>
public sealed class DisplayNamesTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Orders_carry_customer_warehouse_and_actor_names_and_staff_sees_no_customer_name()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        await SetProfileAsync(fixture, kit.Owner.UserId, "sahip@example.com", "Ayşe Yılmaz");
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        await SetProfileAsync(fixture, sales.UserId, "satis.mehmet@example.com", null);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await SetProfileAsync(fixture, staff.UserId, "depo@example.com", "  Depo Ali  ");
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);

        // Created and submitted by sales (no full name → e-mail local part), approved by the owner (full name).
        var draft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 2m)]), HttpStatusCode.Created);
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, draft, "submit"));
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"));

        Assert.Equal(
            ("Sentetik Motor Ltd.", "Ana Depo", "satis.mehmet", "Ayşe Yılmaz"),
            (approved.Order.CustomerName, approved.Order.WarehouseName, approved.Order.CreatedByName, approved.Order.ApprovedByName));
        Assert.Equal(
            [("create", "satis.mehmet"), ("submit", "satis.mehmet"), ("approve", "Ayşe Yılmaz")],
            approved.History.Select(h => (h.Action, h.ChangedByName)));
        Assert.All(approved.History, h => Assert.NotNull(h.ChangedBy));

        var ownerList = await OrdersKit.ReadListAsync(await kit.Owner.GetAsync("/api/v1/orders"));
        var summary = Assert.Single(ownerList.Items);
        Assert.Equal(("Sentetik Motor Ltd.", "Ana Depo"), (summary.CustomerName, summary.WarehouseName));
        Assert.NotNull(summary.GrandTotal);
        var salesList = await OrdersKit.ReadListAsync(await sales.GetAsync("/api/v1/orders"));
        Assert.Equal("Sentetik Motor Ltd.", Assert.Single(salesList.Items).CustomerName);

        // Assignee name on the task summary (list, mine and detail).
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Equal(((Guid?)null, (string?)null), (task.Task.AssignedTo, task.Task.AssigneeName));
        var assigned = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, task.Task.Id, staff.UserId));
        Assert.Equal("Depo Ali", assigned.Task.AssigneeName);
        var listed = await Fulfillment.ReadAsync<PickingTaskPageResponse>(await kit.Owner.GetAsync($"/api/v1/picking-tasks?orderId={approved.Order.Id}"));
        Assert.Equal("Depo Ali", Assert.Single(listed.Items).AssigneeName);
        var mine = await Fulfillment.ReadAsync<MyPickingTasksResponse>(await staff.GetAsync("/api/v1/picking-tasks/mine?scope=assigned"));
        Assert.Equal("Depo Ali", Assert.Single(mine.Items).AssigneeName);

        // warehouse_staff (orders.read K⁸, no customers.read): the order behind its task, without prices and without the
        // customer's name; the warehouse and actor names are operational and stay.
        var seen = await OrdersKit.ReadAsync(await staff.GetAsync($"/api/v1/orders/{approved.Order.Id}"));
        Assert.Equal(((string?)null, "Ana Depo", "satis.mehmet"), (seen.Order.CustomerName, seen.Order.WarehouseName, seen.Order.CreatedByName));
        Assert.Null(seen.Order.Totals);
        Assert.All(seen.Lines, l => Assert.Null(l.UnitPrice));
        var staffList = await OrdersKit.ReadListAsync(await staff.GetAsync("/api/v1/orders"));
        var staffSummary = Assert.Single(staffList.Items);
        Assert.Equal(((string?)null, "Ana Depo", (decimal?)null), (staffSummary.CustomerName, staffSummary.WarehouseName, staffSummary.GrandTotal));
        var raw = await (await staff.GetAsync($"/api/v1/orders/{approved.Order.Id}")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Sentetik Motor", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_without_a_profile_have_no_name_and_foreign_users_never_resolve()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var manager = await kit.Owner.AddMemberAsync(fixture, Roles.Manager); // no profile row at all
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var draft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(manager, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);
        Assert.Equal((manager.UserId, (string?)null), (draft.Order.CreatedBy!.Value, draft.Order.CreatedByName));

        // A user of another tenant with a profile: an assignment to them is refused, and no response ever names them.
        var stranger = await CatalogClient.NewTenantOwnerAsync(fixture);
        await SetProfileAsync(fixture, stranger.UserId, "yabanci@example.com", "Yabancı Kişi");
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        await HttpAssert.ProblemAsync(await Fulfillment.AssignAsync(kit.Owner, task.Task.Id, stranger.UserId), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var text = await (await kit.Owner.GetAsync("/api/v1/picking-tasks")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Yabancı", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_are_resolved_in_one_batch_per_response()
    {
        var calls = new ConcurrentQueue<(string Port, int Ids)>();
        await using var counted = fixture.Derive(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<OrderReferenceNames>();
            services.RemoveAll<IOrderReferenceNames>();
            services.AddScoped<IOrderReferenceNames>(sp => new CountingNames(sp.GetRequiredService<OrderReferenceNames>(), calls));
            services.AddScoped<MembershipTaskAssigneeDirectory>();
            services.RemoveAll<ITaskAssigneeDirectory>();
            services.AddScoped<ITaskAssigneeDirectory>(sp => new CountingAssignees(sp.GetRequiredService<MembershipTaskAssigneeDirectory>(), calls));
        }));

        var kit = await OrdersKit.CreateAsync(fixture);
        var second = await kit.Owner.CreatedAsync<BayiOS.Modules.Customers.Contracts.CustomerDetailResponse>("/api/v1/customers", new { code = "C0002", name = "İkinci Bayi" });
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 20m);
        var users = new List<CatalogClient>();
        for (var i = 0; i < 3; i++)
        {
            var member = await kit.Owner.AddMemberAsync(fixture, Roles.Manager);
            await SetProfileAsync(fixture, member.UserId, $"yonetici{i}@example.com", $"Yönetici {i}");
            users.Add(member);
        }

        var orders = new List<OrderDetailResponse>();
        for (var i = 0; i < 3; i++)
        {
            var draft = await OrdersKit.ReadAsync(
                await kit.CreateResponseAsync(users[i], [OrdersKit.Line(variant, 1m)], customerId: i == 1 ? second.Customer.Id : null), HttpStatusCode.Created);
            var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(users[i], draft, "submit"));
            orders.Add(await OrdersKit.ReadAsync(await OrdersKit.ActAsync(users[(i + 1) % 3], pending, "approve")));
        }

        for (var i = 0; i < 3; i++)
        {
            var task = await Fulfillment.ActiveTaskAsync(kit.Owner, orders[i].Order.Id);
            var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
            await SetProfileAsync(fixture, staff.UserId, null, $"Personel {i}");
            await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, task.Task.Id, staff.UserId));
        }

        var owner = Rebind(counted, kit.Owner);

        calls.Clear();
        var list = await OrdersKit.ReadListAsync(await owner.GetAsync("/api/v1/orders"));
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(["İkinci Bayi", "Sentetik Motor Ltd.", "Sentetik Motor Ltd."], list.Items.Select(o => o.CustomerName!).Order());
        Assert.All(list.Items, o => Assert.Equal("Ana Depo", o.WarehouseName));
        Assert.Equal([("customers", 2), ("warehouses", 1)], calls.OrderBy(c => c.Port));

        calls.Clear();
        var detail = await OrdersKit.ReadAsync(await owner.GetAsync($"/api/v1/orders/{orders[0].Order.Id}"));
        Assert.Equal(("Yönetici 0", "Yönetici 1"), (detail.Order.CreatedByName, detail.Order.ApprovedByName));
        Assert.Equal([("customers", 1), ("users", 2), ("warehouses", 1)], calls.OrderBy(c => c.Port));

        calls.Clear();
        var tasks = await Fulfillment.ReadAsync<PickingTaskPageResponse>(await owner.GetAsync("/api/v1/picking-tasks"));
        Assert.Equal(["Personel 0", "Personel 1", "Personel 2"], tasks.Items.Select(t => t.AssigneeName!).Order());
        Assert.Equal([("assignees", 3)], calls);
    }

    /// <summary>Upserts a synthetic profile row (sign-up creates it via the API; here as superuser for any user).</summary>
    internal static async Task SetProfileAsync(ApiFixture fixture, Guid userId, string? email, string? fullName)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.user_profiles (user_id, email, full_name, created_at) VALUES ($1, $2, $3, now())
            ON CONFLICT (user_id) DO UPDATE SET email = excluded.email, full_name = excluded.full_name, updated_at = now()
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)fullName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static CatalogClient Rebind(WebApplicationFactory<Program> host, CatalogClient client)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.Create(client.UserId));
        return new CatalogClient(http, client.TenantId, client.UserId);
    }

    private sealed class CountingNames(IOrderReferenceNames inner, ConcurrentQueue<(string, int)> calls) : IOrderReferenceNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> CustomerNamesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
        {
            calls.Enqueue(("customers", customerIds.Count));
            return inner.CustomerNamesAsync(customerIds, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, string>> WarehouseNamesAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken)
        {
            calls.Enqueue(("warehouses", warehouseIds.Count));
            return inner.WarehouseNamesAsync(warehouseIds, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
        {
            calls.Enqueue(("users", userIds.Count));
            return inner.UserNamesAsync(userIds, cancellationToken);
        }
    }

    private sealed class CountingAssignees(ITaskAssigneeDirectory inner, ConcurrentQueue<(string, int)> calls) : ITaskAssigneeDirectory
    {
        public Task<string?> FindActiveRoleAsync(Guid userId, CancellationToken cancellationToken) => inner.FindActiveRoleAsync(userId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
        {
            calls.Enqueue(("assignees", userIds.Count));
            return inner.DisplayNamesAsync(userIds, cancellationToken);
        }
    }
}
