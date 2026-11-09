using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.IntegrationTests.Platform;
using BayiOS.IntegrationTests.Security;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Notifications;
using BayiOS.Modules.Notifications.Application;
using BayiOS.Modules.Notifications.Push;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BayiOS.IntegrationTests.Notifications;

/// <summary>
/// Push on picking-task assignment (#44): the assignment writes exactly one outbox event in its transaction; the
/// Notifications subscriber queues one push job; the job sends through <see cref="IPushSender"/> (a fake here — FCM
/// itself is never called in CI) to the assignee's registrations in that tenant only, deletes tokens FCM reports
/// unregistered, retries temporary failures through the job queue and is a no-op while push is disabled. Runs in the
/// global-workers group: its job processor claims every tenant's push jobs (the fake ignores foreign tokens).
/// </summary>
[Collection(GlobalWorkersTestGroup.Name)]
public sealed class PushNotificationTests(ApiFixture fixture)
{
    private const string AssignedEvent = "warehouse.picking_task_assigned.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Assignment_writes_one_event_and_pushes_only_to_the_assignee_in_that_tenant()
    {
        var (kit, taskId) = await PoolTaskAsync();
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var colleague = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var otherTenant = await OrdersKit.CreateAsync(fixture);
        await SecurityProbes.AddMembershipAsync(fixture, otherTenant.TenantId, staff.UserId, Roles.WarehouseStaff);

        var staffHere = DevicesApiTests.NewToken();
        var staffThere = DevicesApiTests.NewToken();
        var colleagueToken = DevicesApiTests.NewToken();
        var ownerToken = DevicesApiTests.NewToken();
        await DevicesApiTests.RegisterAsync(staff, staffHere);
        await DevicesApiTests.RegisterAsync(staff.Targeting(otherTenant.TenantId), staffThere, platform: "ios");
        await DevicesApiTests.RegisterAsync(colleague, colleagueToken);
        await DevicesApiTests.RegisterAsync(kit.Owner, ownerToken);

        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, taskId, staff.UserId));
        var message = Assert.Single(await EventsAsync(kit.TenantId, taskId));

        var sender = new FakePushSender([staffHere, staffThere, colleagueToken, ownerToken]);
        await using var host = Host(sender);
        await DeliverAsync(host.Services, kit.TenantId, message);
        var job = await RunPushJobAsync(host.Services, message.Id);
        Assert.Equal(("succeeded", 1), (job.Status, job.Attempts));

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(staffHere, sent.Token);
        Assert.Equal("Yeni toplama görevi", sent.Message.Title);
        Assert.Equal("Size bir toplama görevi atandı. Açmak için dokunun.", sent.Message.Body);
        Assert.Equal(
            [("taskId", taskId.ToString()), ("tenantId", kit.TenantId.ToString()), ("type", "picking_task_assigned")],
            sent.Message.Data.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => (d.Key, d.Value)));
        Assert.Equal($"task-{taskId:N}", sent.Message.CollapseKey);

        // The job payload carries ids only.
        Assert.Equal(
            ["kind", "taskId", "userId"],
            JsonDocument.Parse(job.Payload).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // The real dispatcher hands this event type to the Notifications subscriber.
        var registry = fixture.Services.GetRequiredService<JobHandlerRegistry>();
        Assert.Contains(registry.Outbox[AssignedEvent], s => s.Name == typeof(PickingTaskAssignedHandler).FullName);
    }

    [Fact]
    public async Task Claiming_a_pool_task_notifies_nobody()
    {
        var (kit, taskId) = await PoolTaskAsync();
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var token = DevicesApiTests.NewToken();
        await DevicesApiTests.RegisterAsync(staff, token);

        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ClaimAsync(staff, taskId)).StatusCode);
        var message = Assert.Single(await EventsAsync(kit.TenantId, taskId));

        var sender = new FakePushSender([token]);
        await using var host = Host(sender);
        await DeliverAsync(host.Services, kit.TenantId, message);
        Assert.Null(await JobAsync(message.Id));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Tokens_reported_unregistered_are_deleted_and_the_others_kept()
    {
        var (kit, taskId) = await PoolTaskAsync();
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var alive = DevicesApiTests.NewToken();
        var gone = DevicesApiTests.NewToken();
        await DevicesApiTests.RegisterAsync(staff, alive);
        await DevicesApiTests.RegisterAsync(staff, gone, platform: "ios");

        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, taskId, staff.UserId));
        var message = Assert.Single(await EventsAsync(kit.TenantId, taskId));
        var sender = new FakePushSender([alive, gone]) { Outcome = t => t == gone ? PushOutcome.Unregistered : PushOutcome.Delivered };
        await using var host = Host(sender);
        await DeliverAsync(host.Services, kit.TenantId, message);

        Assert.Equal("succeeded", (await RunPushJobAsync(host.Services, message.Id)).Status);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(1L, await DevicesApiTests.RowsAsync(fixture, alive));
        Assert.Equal(0L, await DevicesApiTests.RowsAsync(fixture, gone));
    }

    [Fact]
    public async Task Temporary_failures_retry_the_job_with_backoff_and_keep_the_token()
    {
        var (kit, taskId) = await PoolTaskAsync();
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var token = DevicesApiTests.NewToken();
        await DevicesApiTests.RegisterAsync(staff, token);

        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, taskId, staff.UserId));
        var message = Assert.Single(await EventsAsync(kit.TenantId, taskId));
        var sender = new FakePushSender([token]) { Outcome = _ => PushOutcome.Transient };
        await using var host = Host(sender);
        await DeliverAsync(host.Services, kit.TenantId, message);

        var failed = await RunPushJobAsync(host.Services, message.Id);
        Assert.Equal(("queued", 1), (failed.Status, failed.Attempts));
        Assert.Contains("PushDeliveryException", failed.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(token, failed.LastError, StringComparison.Ordinal);
        Assert.Equal(1L, await DevicesApiTests.RowsAsync(fixture, token));

        sender.Outcome = _ => PushOutcome.Delivered;
        await ExecuteAsync("UPDATE platform.jobs SET run_after = now() WHERE dedupe_key = $1", $"push:{message.Id:N}");
        Assert.Equal(("succeeded", 2), ((await RunPushJobAsync(host.Services, message.Id, after: 1)).Status, (await JobAsync(message.Id))!.Attempts));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task Disabled_push_sends_nothing_and_deletes_nothing()
    {
        // The shared host runs with the default Push:Enabled=false.
        var sender = fixture.Services.GetRequiredService<IPushSender>();
        Assert.IsType<DisabledPushSender>(sender);
        Assert.False(sender.IsEnabled);

        var (kit, taskId) = await PoolTaskAsync();
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var token = DevicesApiTests.NewToken();
        await DevicesApiTests.RegisterAsync(staff, token);
        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, taskId, staff.UserId));
        var message = Assert.Single(await EventsAsync(kit.TenantId, taskId));

        await DeliverAsync(fixture.Services, kit.TenantId, message);
        Assert.Equal("succeeded", (await RunPushJobAsync(fixture.Services, message.Id)).Status);
        Assert.Equal(1L, await DevicesApiTests.RowsAsync(fixture, token));
    }

    // ---- helpers ----

    private async Task<(OrdersKit Kit, Guid TaskId)> PoolTaskAsync()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Null(task.Task.AssignedTo);
        return (kit, task.Task.Id);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(FakePushSender sender) =>
        fixture.Derive(builder => builder.ConfigureTestServices(services => services.AddSingleton<IPushSender>(sender)));

    private async Task<List<OutboxRow>> EventsAsync(Guid tenantId, Guid taskId)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT id, payload::text, occurred_at FROM platform.outbox_messages WHERE tenant_id = $1 AND type = $2 AND payload->>'taskId' = $3",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = AssignedEvent });
        command.Parameters.Add(new NpgsqlParameter { Value = taskId.ToString() });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new OutboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return rows;
    }

    /// <summary>What the outbox delivery job does for this subscriber: the handler in the message's tenant, one transaction.</summary>
    private static async Task DeliverAsync(IServiceProvider services, Guid tenantId, OutboxRow row)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.TenantId = tenantId;
        context.CorrelationId = "push-tests";
        var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
        await unitOfWork.BeginAsync(readOnly: false, Ct);
        using var payload = JsonDocument.Parse(row.Payload);
        await scope.ServiceProvider.GetRequiredService<PickingTaskAssignedHandler>()
            .HandleAsync(new OutboxMessage(row.Id, AssignedEvent, tenantId, payload.RootElement.Clone(), "push-tests", row.OccurredAt), Ct);
        await unitOfWork.CommitAsync(Ct);
    }

    /// <summary>Runs push jobs until the job of this message has run <paramref name="after"/> + 1 times or finished.</summary>
    private async Task<JobRow> RunPushJobAsync(IServiceProvider services, Guid messageId, int after = 0)
    {
        var registry = JobHandlerRegistry.FromRegistrations(
            services.GetServices<JobHandlerRegistration>().Where(r => r.Type == NotificationsModule.PushSendJobType), []);
        var processor = new JobProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<JobStore>(),
            registry,
            new JobsOptions { WorkerId = "push-tests", BatchSize = 20 },
            NullLogger<JobProcessor>.Instance);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            var job = await JobAsync(messageId) ?? throw new InvalidOperationException("No push job was queued.");
            if (job.Status is "succeeded" or "dead" || (job.Status == "queued" && job.Attempts > after))
            {
                return job;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Push job of message {messageId} did not run.");
            if (await processor.RunOnceAsync(Ct) == 0)
            {
                await Task.Delay(20, Ct);
            }
        }
    }

    private async Task<JobRow?> JobAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT status, attempts, coalesce(last_error, ''), payload::text FROM platform.jobs WHERE type = $1 AND dedupe_key = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = NotificationsModule.PushSendJobType });
        command.Parameters.Add(new NpgsqlParameter { Value = $"push:{messageId:N}" });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? new JobRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)) : null;
    }

    private async Task ExecuteAsync(string sql, string value)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record OutboxRow(Guid Id, string Payload, DateTimeOffset OccurredAt);

    private sealed record JobRow(string Status, int Attempts, string LastError, string Payload);

    /// <summary>Records sends to the tokens of the test (other tenants' push jobs may run through it too).</summary>
    private sealed class FakePushSender(IReadOnlyCollection<string> tokens) : IPushSender
    {
        private readonly HashSet<string> _tokens = [.. tokens];
        private readonly ConcurrentQueue<(string Token, PushMessage Message)> _sent = new();

        public Func<string, PushOutcome> Outcome { get; set; } = _ => PushOutcome.Delivered;

        public IReadOnlyList<(string Token, PushMessage Message)> Sent => [.. _sent];

        public bool IsEnabled => true;

        public Task<PushOutcome> SendAsync(string token, PushMessage message, CancellationToken cancellationToken)
        {
            if (!_tokens.Contains(token))
            {
                return Task.FromResult(PushOutcome.Delivered);
            }

            _sent.Enqueue((token, message));
            return Task.FromResult(Outcome(token));
        }
    }
}
