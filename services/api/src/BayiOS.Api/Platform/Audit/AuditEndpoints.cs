using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform.Audit;

/// <summary>One audit event (threat-model §7). The IP hash is never returned.</summary>
internal sealed record AuditEventResponse(
    Guid Id,
    string Action,
    string EntityType,
    Guid? EntityId,
    Guid? ActorUserId,
    string? ActorRole,
    IReadOnlyDictionary<string, JsonElement>? Changes,
    string? UserAgent,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

/// <summary>Keyset page metadata (api-strategy §5.1). <c>nextCursor</c> is opaque to clients.</summary>
internal sealed record AuditPageInfo(string? NextCursor, bool HasMore, int Limit);

internal sealed record AuditEventPageResponse(IReadOnlyList<AuditEventResponse> Items, AuditPageInfo Page);

/// <summary>
/// <c>GET /api/v1/audit-events</c> — owner/admin only (<c>audit.read</c>, roles-permissions §3.1). Newest first,
/// keyset pagination on (occurred_at, id); filters by entity, action, actor and time range. RLS scopes the rows to
/// the request tenant; the SQL filters by tenant as well.
/// </summary>
internal static class AuditEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxTextFilter = 100;

    private static readonly string[] AllowedQuery = ["entityType", "entityId", "action", "actorUserId", "from", "to", "limit", "cursor"];

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit-events", ListAuditEvents)
            .WithTags("Audit")
            .WithName("Audit_ListEvents")
            .WithSummary("Audit olayları (yeniden eskiye): keyset sayfalama; entityType/entityId/action/actorUserId/from/to filtresi")
            .RequireAuthorization()
            .WithUnitOfWork()
            .RequirePermission(Permissions.AuditRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return app;
    }

    /// <param name="entityType">Varlık tipi (ör. tenant_settings, inventory_movement).</param>
    /// <param name="action">Olay kodu (ör. tenant.settings_changed).</param>
    /// <param name="from">Bu andan itibaren (dahil), ISO 8601.</param>
    /// <param name="to">Bu ana kadar (hariç), ISO 8601.</param>
    private static async Task<Results<Ok<AuditEventPageResponse>, ProblemHttpResult>> ListAuditEvents(
        HttpRequest http,
        UnitOfWork unitOfWork,
        IRequestContext requestContext,
        string? entityType,
        string? entityId,
        string? action,
        string? actorUserId,
        string? from,
        string? to,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var unknown = http.Query.Keys.Where(k => !AllowedQuery.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            return Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
        }

        if (!TryParseGuid(entityId, out var entity) || !TryParseGuid(actorUserId, out var actor))
        {
            return Malformed("entityId and actorUserId must be UUIDs");
        }

        if (entityType is { Length: > MaxTextFilter } || action is { Length: > MaxTextFilter })
        {
            return Malformed($"entityType and action must be at most {MaxTextFilter} characters");
        }

        if (!TryParseTime(from, out var fromTime) || !TryParseTime(to, out var toTime))
        {
            return Malformed("from and to must be ISO 8601 date-times");
        }

        var pageSize = DefaultLimit;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxLimit))
        {
            return Malformed($"limit must be an integer between 1 and {MaxLimit}");
        }

        AuditCursor? after = null;
        if (cursor is not null)
        {
            if (!AuditCursor.TryDecode(cursor, out var decoded))
            {
                return Malformed("cursor is invalid for this query");
            }

            after = decoded;
        }

        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");
        var sql = new StringBuilder(
            """
            SELECT id, action, entity_type, entity_id, actor_user_id, actor_role, changes::text, user_agent, correlation_id, occurred_at
            FROM audit.audit_events
            WHERE tenant_id = @tenant
            """);
        await using var command = new NpgsqlCommand { Connection = unitOfWork.Connection, Transaction = unitOfWork.Transaction };
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
        if (!string.IsNullOrEmpty(entityType))
        {
            sql.Append(" AND entity_type = @entity_type");
            command.Parameters.Add(new NpgsqlParameter<string>("entity_type", entityType));
        }

        if (entity is { } entityValue)
        {
            sql.Append(" AND entity_id = @entity_id");
            command.Parameters.Add(new NpgsqlParameter<Guid>("entity_id", entityValue));
        }

        if (!string.IsNullOrEmpty(action))
        {
            sql.Append(" AND action = @action");
            command.Parameters.Add(new NpgsqlParameter<string>("action", action));
        }

        if (actor is { } actorValue)
        {
            sql.Append(" AND actor_user_id = @actor");
            command.Parameters.Add(new NpgsqlParameter<Guid>("actor", actorValue));
        }

        if (fromTime is { } fromValue)
        {
            sql.Append(" AND occurred_at >= @from");
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("from", fromValue));
        }

        if (toTime is { } toValue)
        {
            sql.Append(" AND occurred_at < @to");
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("to", toValue));
        }

        if (after is { } afterValue)
        {
            sql.Append(" AND (occurred_at, id) < (@after_at, @after_id)");
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("after_at", afterValue.OccurredAt));
            command.Parameters.Add(new NpgsqlParameter<Guid>("after_id", afterValue.Id));
        }

        sql.Append(" ORDER BY occurred_at DESC, id DESC LIMIT @take");
        command.Parameters.Add(new NpgsqlParameter("take", NpgsqlDbType.Integer) { Value = pageSize + 1 });
        command.CommandText = sql.ToString();

        var rows = new List<AuditEventResponse>(pageSize + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new AuditEventResponse(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetFieldValue<DateTimeOffset>(9)));
            }
        }

        var hasMore = rows.Count > pageSize;
        var items = rows.Take(pageSize).ToList();
        var next = hasMore ? new AuditCursor(items[^1].OccurredAt, items[^1].Id).Encode() : null;
        return TypedResults.Ok(new AuditEventPageResponse(items, new AuditPageInfo(next, hasMore, pageSize)));
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static bool TryParseGuid(string? value, out Guid? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Guid.TryParse(value, out var parsed))
        {
            id = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseTime(string? value, out DateTimeOffset? time)
    {
        time = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            time = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }
}

/// <summary>Opaque keyset cursor: base64url JSON of the last row's (occurred_at, id).</summary>
internal sealed record AuditCursor(DateTimeOffset OccurredAt, Guid Id)
{
    private const string Sort = "audit_desc";

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
        new Payload(Sort, OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), Id)));

    public static bool TryDecode(string value, out AuditCursor cursor)
    {
        cursor = new AuditCursor(default, Guid.Empty);
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value));
            if (payload is null || payload.S != Sort || payload.K is null
                || !DateTimeOffset.TryParseExact(payload.K, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return false;
            }

            cursor = new AuditCursor(at, payload.I);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(string S, string K, Guid I);
}
