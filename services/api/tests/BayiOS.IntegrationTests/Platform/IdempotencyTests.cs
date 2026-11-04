using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Contracts;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>Idempotency-Key behaviour (api-strategy §7, #68), exercised through POST /api/v1/tenants.</summary>
public class IdempotencyTests(ApiFixture fixture)
{
    private const string Path = "/api/v1/tenants";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_key_returns_428()
    {
        var response = await fixture.CreateClientFor(Guid.NewGuid()).PostAsJsonAsync(Path, Body(NewSlug()), Ct);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.PreconditionRequired, "precondition.required");
    }

    [Fact]
    public async Task Overlong_key_returns_400()
    {
        var response = await fixture.CreateClientFor(Guid.NewGuid()).PostIdempotentAsync(Path, Body(NewSlug()), new string('k', 101));

        await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Retry_with_same_key_and_body_replays_the_stored_response_and_creates_one_tenant()
    {
        var client = fixture.CreateClientFor(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();
        var slug = NewSlug();

        var first = await client.PostIdempotentAsync(Path, Body(slug), key);
        var second = await client.PostIdempotentAsync(Path, Body(slug), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", Assert.Single(second.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(first.Content.Headers.ContentType, second.Content.Headers.ContentType);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1L, await CountTenants(slug));
    }

    [Fact]
    public async Task Same_key_with_a_different_body_returns_422()
    {
        var client = fixture.CreateClientFor(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();
        var slug = NewSlug();
        Assert.Equal(HttpStatusCode.Created, (await client.PostIdempotentAsync(Path, Body(slug), key)).StatusCode);

        var other = NewSlug();
        var response = await client.PostIdempotentAsync(Path, Body(other), key);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        Assert.Equal(0L, await CountTenants(other));
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_exactly_one_tenant()
    {
        var client = fixture.CreateClientFor(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();
        var slug = NewSlug();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostIdempotentAsync(Path, Body(slug), key)));

        Assert.Equal(1L, await CountTenants(slug));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToArray();
        Assert.NotEmpty(created);
        Assert.Single(created, r => !r.Headers.Contains("Idempotent-Replayed"));
        var bodies = await Task.WhenAll(created.Select(r => r.Content.ReadAsStringAsync(Ct)));
        Assert.Single(bodies.Distinct());
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            await HttpAssert.ProblemAsync(conflict, HttpStatusCode.Conflict, "idempotency.in_progress");
        }
    }

    [Fact]
    public async Task Duplicate_waiting_on_an_in_flight_request_gets_409_with_retry_after()
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        // Simulates an in-flight first request: an uncommitted claim of the same (user, key).
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO platform.idempotency_keys (id, tenant_id, user_id, key, method, path, request_hash, status, created_at, expires_at)
            VALUES (gen_random_uuid(), NULL, @user, @key, 'POST', '/api/v1/tenants', '\x00', 'in_progress', now(), now() + interval '1 day')
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("key", key);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var slug = NewSlug();
        var response = await fixture.CreateClientFor(userId).PostIdempotentAsync(Path, Body(slug), key);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "idempotency.in_progress");
        Assert.NotNull(response.Headers.RetryAfter);
        await transaction.RollbackAsync(Ct);
        Assert.Equal(0L, await CountTenants(slug));
    }

    [Fact]
    public async Task Keys_are_scoped_per_user_on_tenant_less_endpoints()
    {
        var key = Guid.NewGuid().ToString();
        var slugA = NewSlug();
        var slugB = NewSlug();

        var a = await fixture.CreateClientFor(Guid.NewGuid()).PostIdempotentAsync(Path, Body(slugA), key);
        var b = await fixture.CreateClientFor(Guid.NewGuid()).PostIdempotentAsync(Path, Body(slugB), key);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotent-Replayed"));
        var tenantB = await b.Content.ReadFromJsonAsync<TenantResponse>(Ct);
        Assert.Equal(slugB, tenantB!.Slug);
        Assert.NotEqual((await a.Content.ReadFromJsonAsync<TenantResponse>(Ct))!.Id, tenantB.Id);
    }

    [Fact]
    public async Task Expired_key_behaves_as_new()
    {
        var userId = Guid.NewGuid();
        var client = fixture.CreateClientFor(userId);
        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await client.PostIdempotentAsync(Path, Body(NewSlug()), key)).StatusCode);

        await using (var connection = new NpgsqlConnection(fixture.SuperuserConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var expire = new NpgsqlCommand(
                "UPDATE platform.idempotency_keys SET expires_at = now() - interval '1 second' WHERE user_id = @user AND key = @key",
                connection);
            expire.Parameters.AddWithValue("user", userId);
            expire.Parameters.AddWithValue("key", key);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync(Ct));
        }

        var slug = NewSlug();
        var response = await client.PostIdempotentAsync(Path, Body(slug), key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1L, await CountTenants(slug));
    }

    [Fact]
    public async Task Failed_request_does_not_consume_the_key()
    {
        var client = fixture.CreateClientFor(Guid.NewGuid());
        var key = Guid.NewGuid().ToString();

        var invalid = await client.PostIdempotentAsync(Path, new { name = "x", slug = "Bad Slug!" }, key);
        await HttpAssert.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation.failed");

        var slug = NewSlug();
        var retry = await client.PostIdempotentAsync(Path, Body(slug), key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1L, await CountTenants(slug));
    }

    [Fact]
    public async Task Rls_hides_other_scopes_keys_from_the_app_role()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(fixture.SuperuserConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO platform.idempotency_keys (id, tenant_id, user_id, key, method, path, request_hash, status, created_at, expires_at)
                SELECT gen_random_uuid(), t, u, 'rls', 'POST', '/x', '\x00', 'completed', now(), now() + interval '1 day'
                FROM (VALUES (@ta::uuid, @ua::uuid), (@tb::uuid, @ub::uuid), (NULL::uuid, @ua::uuid), (NULL::uuid, @ub::uuid)) v(t, u)
                """,
                connection);
            seed.Parameters.AddWithValue("ta", tenantA);
            seed.Parameters.AddWithValue("tb", tenantB);
            seed.Parameters.AddWithValue("ua", userA);
            seed.Parameters.AddWithValue("ub", userB);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync(Ct);
        await using var transaction = await app.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)", app, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter { Value = tenantA.ToString() });
            context.Parameters.Add(new NpgsqlParameter { Value = userA.ToString() });
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using var select = new NpgsqlCommand(
            "SELECT coalesce(tenant_id::text, 'user:' || user_id::text) FROM platform.idempotency_keys WHERE key = 'rls' ORDER BY 1",
            app,
            transaction);
        var visible = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                visible.Add(reader.GetString(0));
            }
        }

        Assert.Equal(new[] { tenantA.ToString(), "user:" + userA }.Order(), visible.Order());

        await using var forge = new NpgsqlCommand(
            "INSERT INTO platform.idempotency_keys (id, tenant_id, user_id, key, method, path, request_hash, status, created_at, expires_at) "
            + "VALUES (gen_random_uuid(), $1, $2, 'forged', 'POST', '/x', '\\x00', 'completed', now(), now())",
            app,
            transaction);
        forge.Parameters.Add(new NpgsqlParameter { Value = tenantB });
        forge.Parameters.Add(new NpgsqlParameter { Value = userA });
        var error = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private static object Body(string slug) => new { name = "Idem " + slug, slug };

    private static string NewSlug() => "i-" + Guid.NewGuid().ToString("N")[..12];

    private async Task<long> CountTenants(string slug)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM identity.tenants WHERE slug = @slug", connection);
        command.Parameters.AddWithValue("slug", slug);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
