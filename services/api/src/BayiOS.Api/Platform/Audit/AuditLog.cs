using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform.Audit;

internal sealed class AuditOptions
{
    public const string Section = "Audit";

    /// <summary>HMAC-SHA-256 key floor: at least the hash output size (32 bytes, UTF-8).</summary>
    public const int MinIpHashKeyBytes = 32;

    /// <summary>The database refuses anything shorter (data-policy §4); 730 days ≥ the SQL <c>interval '2 years'</c> floor.</summary>
    public static readonly TimeSpan MinRetention = TimeSpan.FromDays(730);

    /// <summary>
    /// <c>Audit:IpHashKey</c> — server-side secret for the client IP HMAC (data-policy §8.5). Fail-safe: when empty,
    /// no IP hash is stored (an unkeyed SHA-256 of an IPv4 address is trivially reversible). When set it must be at
    /// least <see cref="MinIpHashKeyBytes"/> bytes, checked at startup.
    /// </summary>
    public string? IpHashKey { get; set; }

    /// <summary><c>Audit:Retention</c> — events older than this are purged (default and minimum 730 days).</summary>
    public TimeSpan Retention { get; set; } = MinRetention;

    /// <summary>
    /// <c>Audit:DenialDedupeWindow</c> — at most one access-denial row per (user, tenant, problem code) in this window
    /// (default 60 s, 1 s … 1 h). Repeats inside it are only counted (<c>suppressedRepeats</c> on the next row).
    /// </summary>
    public TimeSpan DenialDedupeWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <c>Audit:DenialDedupeCapacity</c> — tracked (user, tenant, code) keys per instance (default 10 000). When full
    /// and nothing has expired, further denials are only logged: writes stay ≤ capacity per window.
    /// </summary>
    public int DenialDedupeCapacity { get; set; } = 10_000;

    /// <summary><c>Audit:DenialMaxConcurrentWrites</c> — extra pooled connections denial writes may hold at once (default 4).</summary>
    public int DenialMaxConcurrentWrites { get; set; } = 4;

    public bool HasValidDenialSettings =>
        DenialDedupeWindow >= TimeSpan.FromSeconds(1) && DenialDedupeWindow <= TimeSpan.FromHours(1)
        && DenialDedupeCapacity is >= 1 and <= 1_000_000
        && DenialMaxConcurrentWrites is >= 1 and <= 32;

    public static bool IsValidIpHashKey(string? key) =>
        string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) >= MinIpHashKeyBytes;
}

/// <summary>Raw SQL insert into <c>audit.audit_events</c> on the request/job transaction (RLS applies).</summary>
internal sealed class AuditLog(
    UnitOfWork unitOfWork,
    IRequestContext requestContext,
    IHttpContextAccessor httpContextAccessor,
    IOptions<AuditOptions> options,
    TimeProvider clock) : IAuditLog
{
    public const int MaxChangesBytes = 16 * 1024;
    public const int MaxUserAgentLength = 200;

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.EntityType);

        var transaction = unitOfWork.Transaction
            ?? throw new InvalidOperationException("Audit events are written inside the unit of work transaction.");
        var tenantId = requestContext.TenantId
            ?? throw new InvalidOperationException("Audit events need a resolved tenant.");

        string? changes = null;
        if (auditEvent.Changes is { Count: > 0 })
        {
            changes = JsonSerializer.Serialize(auditEvent.Changes, JsonSerializerOptions.Web);
            if (Encoding.UTF8.GetByteCount(changes) > MaxChangesBytes)
            {
                throw new ArgumentException($"Audit changes must stay below {MaxChangesBytes} bytes; record a summary instead.", nameof(auditEvent));
            }
        }

        var http = httpContextAccessor.HttpContext;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_events
                (id, tenant_id, actor_user_id, actor_role, action, entity_type, entity_id, changes, ip_hash, user_agent, correlation_id, occurred_at)
            VALUES (@id, @tenant, @actor, @role, @action, @entity_type, @entity_id, @changes, @ip_hash, @user_agent, @correlation, @occurred_at)
            """,
            unitOfWork.Connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", Guid.CreateVersion7()));
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
        command.Parameters.Add(Nullable("actor", NpgsqlDbType.Uuid, requestContext.UserId));
        command.Parameters.Add(Nullable("role", NpgsqlDbType.Text, requestContext.Role));
        command.Parameters.Add(new NpgsqlParameter<string>("action", auditEvent.Action));
        command.Parameters.Add(new NpgsqlParameter<string>("entity_type", auditEvent.EntityType));
        command.Parameters.Add(Nullable("entity_id", NpgsqlDbType.Uuid, auditEvent.EntityId));
        command.Parameters.Add(Nullable("changes", NpgsqlDbType.Jsonb, changes));
        command.Parameters.Add(Nullable("ip_hash", NpgsqlDbType.Bytea, IpHash(http)));
        command.Parameters.Add(Nullable("user_agent", NpgsqlDbType.Text, UserAgent(http)));
        command.Parameters.Add(Nullable("correlation", NpgsqlDbType.Text, requestContext.CorrelationId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", clock.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Business counters (orders approved, negative stock confirmed, import failures) count committed work only.
        var action = auditEvent.Action;
        unitOfWork.OnCommitted(() => BayiOsMetrics.RecordCommittedAudit(action));
    }

    private byte[]? IpHash(HttpContext? http) => IpHash(options.Value.IpHashKey, http);

    /// <summary>HMAC-SHA-256 of the client IP under <c>Audit:IpHashKey</c>; null without a key or an address.</summary>
    internal static byte[]? IpHash(string? key, HttpContext? http)
    {
        var ip = http?.Connection.RemoteIpAddress;
        if (string.IsNullOrEmpty(key) || ip is null)
        {
            return null;
        }

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(ip.ToString()));
    }

    /// <summary>Shortened, printable-only user agent (threat-model §7).</summary>
    private static string? UserAgent(HttpContext? http)
    {
        var value = http?.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string([.. value.Where(c => c is >= ' ' and <= '~')]).Trim();
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, MaxUserAgentLength)];
    }

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
