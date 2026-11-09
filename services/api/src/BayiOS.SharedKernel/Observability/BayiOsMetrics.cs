using System.Diagnostics;
using System.Diagnostics.Metrics;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.SharedKernel.Observability;

/// <summary>
/// Business and platform instruments of the <c>BayiOS</c> meter (#55, docs/operations/observability.md §3). Exported
/// only when OpenTelemetry is enabled (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>); otherwise the instruments are no-ops.
/// <para>
/// Tag rule: tags are low-cardinality codes only (job type, reason, outcome, route template). Never a tenant id, user
/// id, entity id or any free text — per-tenant analysis belongs to the audit log, not to metrics.
/// </para>
/// </summary>
public static class BayiOsMetrics
{
    public const string MeterName = "BayiOS";

    /// <summary>Activity source for BayiOS spans (exported with the ASP.NET Core / HttpClient / Npgsql spans).</summary>
    public const string ActivitySourceName = "BayiOS";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    /// <summary>Orders approved (committed). Source: audit action <c>order.approved</c>.</summary>
    public static readonly Counter<long> OrdersApproved = Meter.CreateCounter<long>(
        "bayios.orders.approved", "{order}", "Orders approved (stock reserved), counted after commit.");

    /// <summary>Reservations refused because available stock was short. Tag <c>decision</c>: blocked | confirmation_required.</summary>
    public static readonly Counter<long> ReservationShortages = Meter.CreateCounter<long>(
        "bayios.inventory.reservation_shortages", "{reservation}", "Reservations refused for insufficient available stock.");

    /// <summary>Stock changes that went below zero after an explicit confirmation (warn policy), counted after commit.</summary>
    public static readonly Counter<long> NegativeStockConfirmed = Meter.CreateCounter<long>(
        "bayios.inventory.negative_stock_confirmed", "{change}", "Stock changes confirmed below zero (former warn policy). Never emitted since OD-09 (stock never goes below zero): any value is a defect.");

    /// <summary>Jobs moved to <c>dead</c>. Tag <c>job.type</c>.</summary>
    public static readonly Counter<long> JobsDeadLettered = Meter.CreateCounter<long>(
        "bayios.jobs.dead_lettered", "{job}", "Background jobs that exhausted their attempts (status dead).");

    /// <summary>Failed job attempts that will be retried. Tag <c>job.type</c>.</summary>
    public static readonly Counter<long> JobsRetried = Meter.CreateCounter<long>(
        "bayios.jobs.retried", "{attempt}", "Failed background job attempts scheduled for retry.");

    /// <summary>Imports that ended failed. Tag <c>outcome</c>: failed | rollback_failed. Counted after commit.</summary>
    public static readonly Counter<long> ImportsFailed = Meter.CreateCounter<long>(
        "bayios.imports.failed", "{import}", "Imports that ended in failed or rollback_failed.");

    /// <summary>AI parse calls that fell back to the rules parser. Tag <c>reason</c> (FallbackReasons).</summary>
    public static readonly Counter<long> AiFallbacks = Meter.CreateCounter<long>(
        "bayios.ai.fallbacks", "{parse}", "WhatsApp parses that fell back from the AI provider to the rules parser.");

    /// <summary>HTTP responses with status ≥ 500. Tags <c>http.route</c> (template), <c>http.response.status_code</c>.</summary>
    public static readonly Counter<long> HttpServerErrors = Meter.CreateCounter<long>(
        "bayios.http.server_errors", "{response}", "API responses with a 5xx status code.");

    /// <summary>Unhandled exceptions reaching the error-tracking hook. Tag <c>source</c>: http | job.</summary>
    public static readonly Counter<long> UnhandledErrors = Meter.CreateCounter<long>(
        "bayios.errors.unhandled", "{error}", "Unhandled exceptions (HTTP 500 or dead-lettered job).");

    /// <summary>
    /// Access-denial audit outcomes (#89). Tags <c>action</c> (<c>auth.*</c>) and <c>outcome</c>: written | suppressed
    /// (dedupe window) | dropped (dedupe table full / no write slot) | unknown_tenant | failed.
    /// </summary>
    public static readonly Counter<long> AccessDenialAudits = Meter.CreateCounter<long>(
        "bayios.audit.access_denials", "{denial}", "403 access denials seen by the audit writer, by outcome.");

    private static double _queueLagSeconds;
    private static long _queueDepth;

    /// <summary>Age of the oldest due, queued job as last observed by the readiness check.</summary>
    public static readonly ObservableGauge<double> QueueLag = Meter.CreateObservableGauge(
        "bayios.jobs.queue_lag", () => Volatile.Read(ref _queueLagSeconds), "s",
        "Age of the oldest due queued job (last readiness observation).");

    /// <summary>Number of due, queued jobs as last observed by the readiness check.</summary>
    public static readonly ObservableGauge<long> QueueDepth = Meter.CreateObservableGauge(
        "bayios.jobs.queue_depth", () => Interlocked.Read(ref _queueDepth), "{job}",
        "Due queued jobs (last readiness observation).");

    /// <summary>Instrument names of this meter, for documentation and the registration smoke test.</summary>
    public static IReadOnlyList<string> InstrumentNames { get; } =
    [
        OrdersApproved.Name, ReservationShortages.Name, NegativeStockConfirmed.Name, JobsDeadLettered.Name, JobsRetried.Name,
        ImportsFailed.Name, AiFallbacks.Name, HttpServerErrors.Name, UnhandledErrors.Name, AccessDenialAudits.Name, QueueLag.Name,
        QueueDepth.Name,
    ];

    public static void ObserveQueue(TimeSpan lag, long depth)
    {
        Volatile.Write(ref _queueLagSeconds, Math.Max(lag.TotalSeconds, 0d));
        Interlocked.Exchange(ref _queueDepth, depth);
    }

    /// <summary>
    /// Maps a committed audit action to its business counter (called by the audit log after the unit of work commits,
    /// so rolled-back work is never counted).
    /// </summary>
    public static void RecordCommittedAudit(string action)
    {
        switch (action)
        {
            case AuditActions.OrderApproved:
                OrdersApproved.Add(1);
                break;
            case AuditActions.InventoryNegativeStockConfirmed:
                NegativeStockConfirmed.Add(1);
                break;
            case "import.failed":
                ImportsFailed.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                break;
            case "import.rollback_failed":
                ImportsFailed.Add(1, new KeyValuePair<string, object?>("outcome", "rollback_failed"));
                break;
            default:
                break;
        }
    }
}
