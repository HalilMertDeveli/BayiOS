using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace BayiOS.SharedKernel.Platform.Idempotency;

/// <summary>Endpoint requires an <c>Idempotency-Key</c> header (api-strategy §7).</summary>
public sealed class IdempotencyMetadata
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    public const int MaxKeyLength = 100;
}

/// <summary>
/// Endpoint metadata that adds request parts the bound JSON body does not cover (e.g. multipart file bytes) to the
/// <c>Idempotency-Key</c> request hash, so reusing a key with different content is answered with
/// <c>idempotency.key_reused</c> instead of a replay. Runs before the handler; it must leave the request readable.
/// </summary>
public interface IIdempotencyHashContributor
{
    ValueTask ContributeAsync(HttpContext context, IncrementalHash hash, CancellationToken cancellationToken);
}

/// <summary>
/// <c>Idempotency-Key</c> handling (api-strategy §7) inside the request's <see cref="UnitOfWork"/> transaction:
/// the key row is claimed first (<c>INSERT … ON CONFLICT</c>), the handler runs, and a successful response is stored
/// in the same transaction as the business write — both commit or neither does. A concurrent duplicate blocks on the
/// unique index until the first transaction ends, then replays its response (or 409 after <see cref="LockTimeout"/>).
/// Error results roll back together with the claim, so the key stays usable for a corrected retry.
/// </summary>
internal static class IdempotencyFilter
{
    public const string HeaderName = IdempotencyMetadata.HeaderName;
    public const string ReplayedHeaderName = IdempotencyMetadata.ReplayedHeaderName;
    public const int MaxKeyLength = IdempotencyMetadata.MaxKeyLength;

    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>How long a duplicate waits for an in-flight request with the same key before answering 409.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] ReplayedHeaders = ["Content-Type", "Location", "ETag"];

    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var values = http.Request.Headers[HeaderName];
        if (values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status428PreconditionRequired, "precondition.required", $"{HeaderName} header is required"));
        }

        var key = values[0]!;
        if (values.Count > 1 || key.Length > MaxKeyLength)
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status400BadRequest, "request.malformed", $"{HeaderName} must be a single value of at most {MaxKeyLength} characters"));
        }

        var services = http.RequestServices;
        var unitOfWork = services.GetRequiredService<UnitOfWork>();
        var requestContext = services.GetRequiredService<IRequestContext>();
        var store = new IdempotencyStore(
            unitOfWork,
            requestContext.TenantId,
            requestContext.UserId ?? throw new InvalidOperationException("Idempotency requires an authenticated user."),
            key);

        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var path = http.Request.Path.Value ?? string.Empty;
        var hash = await RequestHashAsync(context, path, http.RequestAborted);
        var cancellationToken = http.RequestAborted;

        Guid? claimedId;
        try
        {
            claimedId = await store.TryClaimAsync(http.Request.Method, path, hash, now, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return InProgress(http);
        }

        if (claimedId is not { } id)
        {
            var existing = await store.FindAsync(cancellationToken);
            if (existing is null || existing.Status != "completed")
            {
                return InProgress(http);
            }

            if (existing.UserId != store.UserId || !CryptographicOperations.FixedTimeEquals(existing.RequestHash, hash))
            {
                return TypedResults.Problem(ApiProblems.Create(
                    StatusCodes.Status422UnprocessableEntity,
                    "idempotency.key_reused",
                    $"{HeaderName} was already used for a different request"));
            }

            return new StoredHttpResult(existing.ResponseStatus!.Value, existing.Headers(), existing.ResponseBody ?? [], replayed: true);
        }

        var result = await next(context);
        if (EndpointResults.StatusCodeOf(result) is < 200 or >= 300)
        {
            // The unit of work rolls back the claim with the business write.
            return result;
        }

        if (!unitOfWork.IsActive)
        {
            throw new InvalidOperationException("The idempotency filter must run inside the unit-of-work filter.");
        }

        var response = await BufferAsync(http, result);
        await store.CompleteAsync(id, response, CancellationToken.None);
        return response;
    }

    private static ProblemHttpResult InProgress(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "1";
        return TypedResults.Problem(ApiProblems.Create(
            StatusCodes.Status409Conflict, "idempotency.in_progress", "A request with this Idempotency-Key is still in progress"));
    }

    /// <summary>
    /// SHA-256 over method, concrete path (route values included), the bound JSON body and whatever the endpoint's
    /// <see cref="IIdempotencyHashContributor"/> metadata adds (e.g. multipart file bytes).
    /// </summary>
    private static async Task<byte[]> RequestHashAsync(EndpointFilterInvocationContext context, string path, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var bodyType = http.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
        var body = bodyType is null ? null : context.Arguments.FirstOrDefault(a => a is not null && bodyType.IsInstanceOfType(a));
        var json = http.RequestServices.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{http.Request.Method}\n{path}\n"));
        if (body is not null)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), json));
        }

        foreach (var contributor in http.GetEndpoint()?.Metadata.GetOrderedMetadata<IIdempotencyHashContributor>() ?? [])
        {
            await contributor.ContributeAsync(http, hash, cancellationToken);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Executes the result into memory so the exact status, headers and bytes can be stored and replayed.</summary>
    private static async Task<StoredHttpResult> BufferAsync(HttpContext http, object? result)
    {
        if (result is not IResult httpResult)
        {
            throw new InvalidOperationException("Idempotent endpoints must return an IResult.");
        }

        var original = http.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new MemoryStream();
        var capture = new StreamResponseBodyFeature(buffer);
        http.Features.Set<IHttpResponseBodyFeature>(capture);
        try
        {
            await httpResult.ExecuteAsync(http);
            await capture.CompleteAsync();
        }
        finally
        {
            http.Features.Set(original);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ReplayedHeaders)
        {
            if (http.Response.Headers.TryGetValue(name, out var value) && value.Count > 0)
            {
                headers[name] = value.ToString();
            }
        }

        return new StoredHttpResult(http.Response.StatusCode, headers, buffer.ToArray(), replayed: false);
    }
}

/// <summary>A response captured for idempotency; written verbatim (plus <c>Idempotent-Replayed</c> on replays).</summary>
internal sealed class StoredHttpResult(int statusCode, IReadOnlyDictionary<string, string> headers, byte[] body, bool replayed)
    : IResult, IStatusCodeHttpResult
{
    public int? StatusCode => statusCode;

    public IReadOnlyDictionary<string, string> Headers => headers;

    public ReadOnlyMemory<byte> Body => body;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = statusCode;
        foreach (var (name, value) in headers)
        {
            response.Headers[name] = value;
        }

        if (replayed)
        {
            response.Headers[IdempotencyFilter.ReplayedHeaderName] = "true";
        }

        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body);
    }
}

/// <summary>Raw SQL access to <c>platform.idempotency_keys</c> on the request transaction (RLS applies).</summary>
internal sealed class IdempotencyStore(UnitOfWork unitOfWork, Guid? tenantId, Guid userId, string key)
{
    private const string Columns = "id, tenant_id, user_id, key, method, path, request_hash, status, created_at, expires_at";

    public Guid UserId => userId;

    private string ScopeFilter => tenantId is null ? "tenant_id IS NULL AND user_id = @scope" : "tenant_id = @scope";

    private string ConflictTarget => tenantId is null ? "(user_id, key) WHERE tenant_id IS NULL" : "(tenant_id, key) WHERE tenant_id IS NOT NULL";

    /// <summary>Inserts the key, or takes over an expired one. Returns null when a live row already holds the key.</summary>
    public async Task<Guid?> TryClaimAsync(string method, string path, byte[] hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SetLockTimeoutAsync($"{(int)IdempotencyFilter.LockTimeout.TotalMilliseconds}ms", cancellationToken);

        await using var command = Command($"""
            INSERT INTO platform.idempotency_keys AS k ({Columns})
            VALUES (@id, @tenant, @user, @key, @method, @path, @hash, 'in_progress', @now, @expires)
            ON CONFLICT {ConflictTarget} DO UPDATE SET
                user_id = EXCLUDED.user_id, method = EXCLUDED.method, path = EXCLUDED.path,
                request_hash = EXCLUDED.request_hash, status = 'in_progress', response_status = NULL,
                response_headers = NULL, response_body = NULL, created_at = EXCLUDED.created_at, expires_at = EXCLUDED.expires_at
            WHERE k.expires_at <= EXCLUDED.created_at
            RETURNING k.id
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", Guid.CreateVersion7()));
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)tenantId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<Guid>("user", userId));
        command.Parameters.Add(new NpgsqlParameter<string>("key", key));
        command.Parameters.Add(new NpgsqlParameter<string>("method", method));
        command.Parameters.Add(new NpgsqlParameter<string>("path", path));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("hash", hash));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", now));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("expires", now + IdempotencyFilter.Retention));
        var id = await command.ExecuteScalarAsync(cancellationToken);

        await SetLockTimeoutAsync(null, cancellationToken);
        return id as Guid?;
    }

    public async Task<StoredKey?> FindAsync(CancellationToken cancellationToken)
    {
        await using var command = Command($"""
            SELECT user_id, request_hash, status, response_status, response_headers::text, response_body
            FROM platform.idempotency_keys
            WHERE {ScopeFilter} AND key = @key
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("scope", tenantId ?? userId));
        command.Parameters.Add(new NpgsqlParameter<string>("key", key));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredKey(
            reader.GetGuid(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5));
    }

    public async Task CompleteAsync(Guid id, StoredHttpResult response, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE platform.idempotency_keys
            SET status = 'completed', response_status = @status, response_headers = @headers, response_body = @body
            WHERE id = @id
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        command.Parameters.Add(new NpgsqlParameter<int>("status", response.StatusCode!.Value));
        command.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(response.Headers) });
        command.Parameters.Add(new NpgsqlParameter<byte[]>("body", response.Body.ToArray()));

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The claimed idempotency key could not be completed.");
        }
    }

    /// <summary>Bounds the wait on a concurrent claim; <c>null</c> restores the default for the rest of the transaction.</summary>
    private async Task SetLockTimeoutAsync(string? value, CancellationToken cancellationToken)
    {
        await using var command = Command(value is null ? "SET LOCAL lock_timeout TO DEFAULT" : $"SET LOCAL lock_timeout = '{value}'");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlCommand Command(string sql) =>
        new(sql, unitOfWork.Connection, unitOfWork.Transaction ?? throw new InvalidOperationException("The unit of work has not started."));
}

internal sealed record StoredKey(Guid UserId, byte[] RequestHash, string Status, int? ResponseStatus, string? ResponseHeaders, byte[]? ResponseBody)
{
    public IReadOnlyDictionary<string, string> Headers() =>
        ResponseHeaders is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(ResponseHeaders) ?? [];
}
