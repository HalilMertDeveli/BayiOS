using System.Globalization;
using BayiOS.Modules.Notifications.Infrastructure.Persistence;
using BayiOS.Modules.Notifications.Push;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Modules.Notifications.Application;

/// <summary>What a push is about; the app routes on <c>data.type</c>.</summary>
internal static class PushKinds
{
    public const string PickingTaskAssigned = "picking_task_assigned";
}

/// <summary>Payload of a <see cref="PushSendJob"/>: ids only (ADR 0006 — never secrets or personal data).</summary>
internal sealed record PushSendPayload(string Kind, Guid UserId, Guid TaskId);

/// <summary>Turkish texts of the push messages. No order numbers, customer names, quantities or prices.</summary>
internal static class PushMessages
{
    public static PushMessage For(PushSendPayload payload, Guid tenantId) => payload.Kind switch
    {
        PushKinds.PickingTaskAssigned => new PushMessage(
            "Yeni toplama görevi",
            "Size bir toplama görevi atandı. Açmak için dokunun.",
            new Dictionary<string, string>
            {
                ["type"] = PushKinds.PickingTaskAssigned,
                ["taskId"] = payload.TaskId.ToString("D", CultureInfo.InvariantCulture),
                ["tenantId"] = tenantId.ToString("D", CultureInfo.InvariantCulture),
            },
            $"task-{payload.TaskId:N}"),
        _ => throw new InvalidOperationException($"Unknown push kind '{payload.Kind}'."),
    };
}

/// <summary>
/// Subscriber of <c>warehouse.picking_task_assigned.v1</c> (Warehouse publishes it in the assignment's transaction):
/// queues one <see cref="PushSendJob"/> for the assignee. A self-claim from the pool (<c>assignedBy</c> = assignee)
/// notifies nobody. Runs as an outbox delivery job, so it is retried by the queue and idempotent per message id.
/// </summary>
internal sealed class PickingTaskAssignedHandler(IBackgroundJobs jobs) : IOutboxHandler
{
    /// <summary>Must equal Warehouse's <c>FulfillmentEvents.PickingTaskAssigned</c> (unit-tested).</summary>
    public const string MessageType = "warehouse.picking_task_assigned.v1";

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var assigned = message.PayloadAs<TaskAssigned>();
        if (message.TenantId is null || assigned.AssignedTo is not { } userId || assigned.AssignedTo == assigned.AssignedBy)
        {
            return;
        }

        await jobs.EnqueueAsync(
            new JobRequest(
                PushSendJob.Type,
                new PushSendPayload(PushKinds.PickingTaskAssigned, userId, assigned.TaskId),
                MaxAttempts: 5,
                DedupeKey: $"push:{message.Id:N}"),
            cancellationToken);
    }

    private sealed record TaskAssigned(Guid TaskId, Guid? AssignedTo, Guid? AssignedBy);
}

/// <summary>Some devices could not be reached for a temporary reason: the job is retried with backoff.</summary>
internal sealed class PushDeliveryException(int transient, int total)
    : Exception($"{transient} of {total} push deliveries failed temporarily.");

/// <summary>
/// <c>notifications.push_send</c>: sends one message to every registration of the user <b>in the job's tenant</b>
/// (RLS + query), without holding a connection while FCM answers (<see cref="IExternalCallJobHandler"/>, ADR 0006
/// #121). Tokens FCM reports unregistered are deleted; temporary failures retry the job (the queue's backoff). A retry
/// resends to every device; the collapse key makes the device replace, not stack, the notification. Disabled push →
/// the no-op sender, nothing is deleted.
/// </summary>
internal sealed partial class PushSendJob(IJobTransactions transactions, IPushSender sender, ILogger<PushSendJob> logger) : IExternalCallJobHandler
{
    public const string Type = NotificationsModule.PushSendJobType;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.TenantId is not { } tenantId)
        {
            return; // tenant job only
        }

        var payload = job.PayloadAs<PushSendPayload>();
        var devices = await transactions.RunAsync(
            async (services, ct) => await services.GetRequiredService<NotificationsDbContext>().Devices.AsNoTracking()
                .Where(d => d.UserId == payload.UserId)
                .OrderBy(d => d.Id)
                .Select(d => new { d.Id, d.Token })
                .ToListAsync(ct),
            cancellationToken);
        if (devices.Count == 0)
        {
            return;
        }

        var message = PushMessages.For(payload, tenantId);
        var unregistered = new List<Guid>();
        var counts = new Dictionary<PushOutcome, int>();
        foreach (var device in devices)
        {
            var outcome = await sender.SendAsync(device.Token, message, cancellationToken);
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
            if (outcome == PushOutcome.Unregistered)
            {
                unregistered.Add(device.Id);
            }
        }

        var (delivered, skipped, transient, failed) = (counts.GetValueOrDefault(PushOutcome.Delivered), counts.GetValueOrDefault(PushOutcome.Skipped),
            counts.GetValueOrDefault(PushOutcome.Transient), counts.GetValueOrDefault(PushOutcome.Failed));
        LogSent(logger, job.Id, payload.Kind, devices.Count, delivered, skipped, unregistered.Count, transient, failed);

        Task Forget(IServiceProvider services, CancellationToken ct) => DeleteAsync(services, unregistered, ct);
        if (transient > 0)
        {
            if (unregistered.Count > 0)
            {
                await transactions.RunAsync(async (services, ct) => { await Forget(services, ct); return true; }, cancellationToken);
            }

            throw new PushDeliveryException(transient, devices.Count);
        }

        await transactions.CompleteAsync(Forget, cancellationToken);
    }

    private static async Task DeleteAsync(IServiceProvider services, List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var db = services.GetRequiredService<NotificationsDbContext>();
        await db.JoinTransactionAsync(cancellationToken);
        await db.Devices.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push job {JobId} ({Kind}): {Devices} device(s), {Delivered} delivered, {Skipped} skipped (disabled), {Unregistered} unregistered (deleted), {Transient} temporary failures, {Failed} rejected")]
    private static partial void LogSent(
        ILogger logger, Guid jobId, string kind, int devices, int delivered, int skipped, int unregistered, int transient, int failed);
}

/// <summary>
/// <c>notifications.device_cleanup</c> (tenant-less maintenance job, scheduled by the platform worker): deletes
/// registrations not refreshed for <see cref="PushOptions.StaleAfterDays"/> (the app re-registers on every start).
/// Runs through <c>notifications.purge_stale_device_tokens</c> (SECURITY DEFINER; the owner may only see and delete
/// rows older than the 30-day floor). Batched, oldest first; idempotent.
/// </summary>
internal sealed partial class StaleDeviceCleanupJob(UnitOfWork unitOfWork, IOptions<PushOptions> options, ILogger<StaleDeviceCleanupJob> logger) : IJobHandler
{
    public const string Type = NotificationsModule.DeviceCleanupJobType;
    public const int BatchSize = 1000;
    public const int MaxBatches = 50;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.TenantId is not null)
        {
            return; // system job only
        }

        var retention = TimeSpan.FromDays(options.Value.StaleAfterDays);
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT notifications.purge_stale_device_tokens(@retention, @limit)", unitOfWork.Connection, unitOfWork.Transaction);
            command.Parameters.Add(new NpgsqlParameter<TimeSpan>("retention", retention));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", BatchSize));
            var deleted = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        LogPurged(logger, total);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} stale push device registrations")]
    private static partial void LogPurged(ILogger logger, int count);
}
