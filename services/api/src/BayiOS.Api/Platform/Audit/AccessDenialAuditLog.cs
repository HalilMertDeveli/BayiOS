using System.Data;
using System.Text.Json;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform.Audit;

/// <summary>
/// <see cref="IAccessDenialAudit"/> (#89): one short transaction on its own pooled <c>bayios_app</c> connection per
/// written row — the request's unit of work rolls back on a 403, this row does not. The transaction sets
/// <c>app.user_id</c> / <c>app.tenant_id</c> with <c>set_config(…, true)</c>, so the table's RLS policy applies to the
/// insert exactly as to every other audit row.
/// <para>
/// Bounded (threat-model §7): <see cref="AccessDenialDeduplicator"/> allows one row per (user, tenant, code) per
/// window; at most <see cref="AuditOptions.DenialMaxConcurrentWrites"/> writes hold a connection at once (a denial that
/// cannot get a slot within <see cref="SlotWait"/> is logged, not written); each write has a
/// <see cref="WriteTimeout"/>. A membership denial names an unverified tenant: the insert selects from
/// <c>identity.tenants</c>, so a tenant id that does not exist writes nothing.
/// </para>
/// </summary>
internal sealed partial class AccessDenialAuditLog : IAccessDenialAudit, IDisposable
{
    public const string EntityType = "access";
    public const int MaxRouteLength = 200;

    public static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptions<AuditOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AccessDenialAuditLog> _logger;
    private readonly SemaphoreSlim _slots;

    public AccessDenialAuditLog(NpgsqlDataSource dataSource, IOptions<AuditOptions> options, TimeProvider clock, ILogger<AccessDenialAuditLog> logger)
    {
        _dataSource = dataSource;
        _options = options;
        _clock = clock;
        _logger = logger;
        var settings = options.Value;
        Deduplicator = new AccessDenialDeduplicator(settings.DenialDedupeWindow, settings.DenialDedupeCapacity, clock);
        _slots = new SemaphoreSlim(settings.DenialMaxConcurrentWrites, settings.DenialMaxConcurrentWrites);
    }

    internal AccessDenialDeduplicator Deduplicator { get; }

    public async Task RecordAsync(HttpContext context, AccessDenial denial)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(denial);

        // Only a validated JWT sets the user (TenantContextMiddleware); a 401 never reaches here.
        var requestContext = context.RequestServices.GetRequiredService<IRequestContext>();
        if (requestContext.UserId is not { } userId)
        {
            return;
        }

        var key = new DenialKey(userId, denial.TenantId, denial.Code);
        var decision = Deduplicator.Decide(key, out var suppressedRepeats);
        if (decision != DenialDecision.Write)
        {
            Count(decision == DenialDecision.Suppressed ? "suppressed" : "dropped", denial.Action);
            if (decision == DenialDecision.Dropped)
            {
                LogDropped(_logger, denial.Action, denial.Code, userId, denial.TenantId);
            }

            return;
        }

        // Not RequestAborted: a caller that hangs up must not be able to skip its own denial row.
        if (!await _slots.WaitAsync(SlotWait, CancellationToken.None))
        {
            Deduplicator.Forget(key, suppressedRepeats);
            Count("dropped", denial.Action);
            LogDropped(_logger, denial.Action, denial.Code, userId, denial.TenantId);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            var written = await WriteAsync(context, requestContext, userId, denial, suppressedRepeats, timeout.Token);
            Count(written ? "written" : "unknown_tenant", denial.Action);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            // The 403 is answered regardless; the server log keeps the event (ids only, data-policy §8.1).
            Deduplicator.Forget(key, suppressedRepeats);
            Count("failed", denial.Action);
            LogWriteFailed(_logger, ex, denial.Action, denial.Code, userId, denial.TenantId);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();

    private async Task<bool> WriteAsync(
        HttpContext context, IRequestContext requestContext, Guid userId, AccessDenial denial, int suppressedRepeats, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (var settings = new NpgsqlCommand(
            "SELECT set_config('app.user_id', $1, true), set_config('app.tenant_id', $2, true)", connection, transaction))
        {
            settings.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text, TypedValue = userId.ToString() });
            settings.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text, TypedValue = denial.TenantId.ToString() });
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        // FROM identity.tenants: an unknown tenant id (membership denial, unverified header) inserts nothing. The
        // membership status (none / invited / disabled; active for an inactive tenant) is read under the same RLS.
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_events
                (id, tenant_id, actor_user_id, actor_role, action, entity_type, entity_id, changes, ip_hash, user_agent, correlation_id, occurred_at)
            SELECT @id, t.id, @actor, @role, @action, @entity_type, NULL,
                   CASE WHEN @with_membership
                        THEN @changes || jsonb_build_object('membershipStatus', coalesce(
                            (SELECT m.status FROM identity.memberships m WHERE m.tenant_id = t.id AND m.user_id = @actor LIMIT 1), 'none'))
                        ELSE @changes END,
                   @ip_hash, NULL, @correlation, @occurred_at
            FROM identity.tenants t
            WHERE t.id = @tenant
            """,
            connection,
            transaction);
        insert.Parameters.Add(new NpgsqlParameter<Guid>("id", Guid.CreateVersion7()));
        insert.Parameters.Add(new NpgsqlParameter<Guid>("tenant", denial.TenantId));
        insert.Parameters.Add(new NpgsqlParameter<Guid>("actor", userId));
        insert.Parameters.Add(Nullable("role", NpgsqlDbType.Text, denial.Role));
        insert.Parameters.Add(new NpgsqlParameter<string>("action", denial.Action));
        insert.Parameters.Add(new NpgsqlParameter<string>("entity_type", EntityType));
        insert.Parameters.Add(new NpgsqlParameter<bool>("with_membership", denial.Action == AccessDenialActions.MembershipDenied));
        insert.Parameters.Add(new NpgsqlParameter("changes", NpgsqlDbType.Jsonb) { Value = Changes(context, denial, suppressedRepeats) });
        insert.Parameters.Add(Nullable("ip_hash", NpgsqlDbType.Bytea, AuditLog.IpHash(_options.Value.IpHashKey, context)));
        insert.Parameters.Add(Nullable("correlation", NpgsqlDbType.Text, requestContext.CorrelationId));
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", _clock.GetUtcNow()));
        var rows = await insert.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return rows == 1;
    }

    /// <summary>
    /// Server-side values only: the problem code, method, route <b>template</b> (never the raw path or query), the
    /// permission / condition / feature codes and the repeat count. No user agent, body or header text.
    /// </summary>
    internal static string Changes(HttpContext context, AccessDenial denial, int suppressedRepeats)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = denial.Code,
            ["method"] = context.Request.Method.Length <= 10 ? context.Request.Method : null,
            ["route"] = RouteOf(context),
        };
        if (denial.Permissions is { Count: > 0 } permissions)
        {
            changes["permissions"] = permissions;
        }

        if (denial.Condition is not null)
        {
            changes["condition"] = denial.Condition;
        }

        if (denial.Feature is not null)
        {
            changes["feature"] = denial.Feature;
        }

        if (suppressedRepeats > 0)
        {
            changes["suppressedRepeats"] = suppressedRepeats;
        }

        return JsonSerializer.Serialize(changes, JsonSerializerOptions.Web);
    }

    private static string? RouteOf(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is { } route
            ? route[..Math.Min(route.Length, MaxRouteLength)]
            : null;

    private static void Count(string outcome, string action) =>
        BayiOsMetrics.AccessDenialAudits.Add(1, new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("action", action));

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access denial {Action} ({Code}) of user {UserId} in tenant {TenantId} not audited: dedupe table full or no write slot")]
    private static partial void LogDropped(ILogger logger, string action, string code, Guid userId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Access denial {Action} ({Code}) of user {UserId} in tenant {TenantId} could not be audited")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string action, string code, Guid userId, Guid tenantId);
}
