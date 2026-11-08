using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Security;
using BayiOS.Modules.Notifications.Contracts;
using BayiOS.SharedKernel.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BayiOS.IntegrationTests.Notifications;

/// <summary>
/// Push device registrations (#44): <c>/api/v1/devices</c> is scoped to tenant + user (query and RLS), a token belongs
/// to one (tenant, user) at a time and moves on re-registration, the token is never returned or logged, and the
/// registrations per user are bounded. Synthetic tokens only.
/// </summary>
public sealed class DevicesApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string NewToken() => $"test-{Guid.NewGuid():N}:APA91b{Guid.NewGuid():N}";

    internal static async Task<DeviceResponse> RegisterAsync(CatalogClient client, string token, string platform = "android", string? key = null)
    {
        var response = await client.PostAsync("/api/v1/devices", new { token, platform, appVersion = "0.1.0+1", locale = "tr-TR" }, key);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        return System.Text.Json.JsonSerializer.Deserialize<DeviceResponse>(text, System.Text.Json.JsonSerializerOptions.Web)!;
    }

    internal static async Task<IReadOnlyList<DeviceResponse>> ListAsync(CatalogClient client)
    {
        var response = await client.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeviceListResponse>(Ct))!.Items;
    }

    internal static async Task<long> RowsAsync(ApiFixture fixture, string token)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM notifications.device_tokens WHERE token_sha256 = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = SHA256.HashData(Encoding.UTF8.GetBytes(token)) });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Register_refresh_list_and_delete_own_registration()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var token = NewToken();

        var first = await RegisterAsync(owner, token);
        Assert.Equal(("android", "0.1.0+1", "tr-TR"), (first.Platform, first.AppVersion, first.Locale));
        Assert.Equal(first.RegisteredAt, first.LastSeenAt);

        // Same token again (app start, token refresh): the same registration, refreshed — not a second row.
        var again = await RegisterAsync(owner, token, platform: "ios");
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("ios", again.Platform);
        Assert.True(again.LastSeenAt >= first.LastSeenAt);
        Assert.Equal(first.Id, Assert.Single(await ListAsync(owner)).Id);
        Assert.Equal(1L, await RowsAsync(fixture, token));

        // Idempotent replay of a lost answer: the stored response, no new write.
        var key = Guid.NewGuid().ToString();
        var replayed = await RegisterAsync(owner, token, key: key);
        Assert.Equal(replayed, await RegisterAsync(owner, token, key: key));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/devices/{first.Id}", null)).StatusCode);
        Assert.Empty(await ListAsync(owner));
        Assert.Equal(0L, await RowsAsync(fixture, token));
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/devices/{first.Id}", null), HttpStatusCode.NotFound, "resource.not_found");
    }

    [Theory]
    [InlineData("short", "android", "token")]
    [InlineData("has spaces in it but is long enough to pass the length check", "android", "token")]
    [InlineData("valid-token-0123456789abcdefghijklmnopqrstuv", "web", "platform")]
    [InlineData("valid-token-0123456789abcdefghijklmnopqrstuv", "", "platform")]
    [InlineData("", "android", "token")]
    public async Task Invalid_registrations_are_refused_with_field_errors(string token, string platform, string field)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var problem = await HttpAssert.ProblemJsonAsync(
            await owner.PostAsync("/api/v1/devices", new { token, platform }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
        Assert.Empty(await ListAsync(owner));
    }

    [Fact]
    public async Task Every_role_registers_but_only_with_an_active_membership_and_the_idempotency_key()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        foreach (var role in Roles.All.Where(r => r != Roles.Owner))
        {
            var member = await owner.AddMemberAsync(fixture, role);
            await RegisterAsync(member, NewToken());
        }

        var outsider = await CatalogClient.NewTenantOwnerAsync(fixture);
        await HttpAssert.ProblemAsync(
            await outsider.Targeting(owner.TenantId).PostAsync("/api/v1/devices", new { token = NewToken(), platform = "android" }),
            HttpStatusCode.Forbidden,
            "tenant.access_denied");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices") { Content = JsonContent.Create(new { token = NewToken(), platform = "android" }) };
        request.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await owner.Http.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_user_sees_and_deletes_only_their_own_registrations()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var staff = await owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var mine = await RegisterAsync(owner, NewToken());
        var theirs = await RegisterAsync(staff, NewToken());

        Assert.Equal(mine.Id, Assert.Single(await ListAsync(owner)).Id);
        Assert.Equal(theirs.Id, Assert.Single(await ListAsync(staff)).Id);

        // Same tenant, other user: invisible (query and RLS), so 404 and nothing deleted.
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/devices/{theirs.Id}", null), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Single(await ListAsync(staff));

        // Other tenant (the owner of B is also a member of A's tenant here: the header decides).
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        await SecurityProbes.AddMembershipAsync(fixture, other.TenantId, owner.UserId, Roles.Owner);
        await HttpAssert.ProblemAsync(
            await owner.Targeting(other.TenantId).DeleteAsync($"/api/v1/devices/{mine.Id}", null), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Empty(await ListAsync(owner.Targeting(other.TenantId)));
        Assert.Single(await ListAsync(owner));
    }

    [Fact]
    public async Task A_token_registered_by_another_user_or_in_another_tenant_moves_there()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var staff = await owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var token = NewToken();

        await RegisterAsync(owner, token);
        var moved = await RegisterAsync(staff, token); // the phone was handed to a colleague
        Assert.Empty(await ListAsync(owner));
        Assert.Equal(moved.Id, Assert.Single(await ListAsync(staff)).Id);
        Assert.Equal(1L, await RowsAsync(fixture, token));

        // The colleague switches to another business on the same phone: the registration follows the tenant.
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        await SecurityProbes.AddMembershipAsync(fixture, other.TenantId, staff.UserId, Roles.WarehouseStaff);
        var inOther = await RegisterAsync(staff.Targeting(other.TenantId), token);
        Assert.NotEqual(moved.Id, inOther.Id);
        Assert.Empty(await ListAsync(staff));
        Assert.Equal(inOther.Id, Assert.Single(await ListAsync(staff.Targeting(other.TenantId))).Id);
        Assert.Equal(1L, await RowsAsync(fixture, token));
    }

    [Fact]
    public async Task Concurrent_registrations_of_one_token_leave_exactly_one_owner()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var members = new List<CatalogClient> { owner };
        for (var i = 0; i < 5; i++)
        {
            members.Add(await owner.AddMemberAsync(fixture, Roles.WarehouseStaff));
        }

        var token = NewToken();
        var responses = await Task.WhenAll(members.Select(m => Task.Run(() => m.PostAsync("/api/v1/devices", new { token, platform = "android" }), Ct)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1L, await RowsAsync(fixture, token));
        var owners = 0;
        foreach (var member in members)
        {
            owners += (await ListAsync(member)).Count;
        }

        Assert.Equal(1, owners);
    }

    [Fact]
    public async Task Registrations_per_user_are_bounded_least_recently_seen_dropped()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var tokens = Enumerable.Range(0, 12).Select(_ => NewToken()).ToList();
        foreach (var token in tokens)
        {
            await RegisterAsync(owner, token);
        }

        Assert.Equal(10, (await ListAsync(owner)).Count);
        Assert.Equal(0L, await RowsAsync(fixture, tokens[0]));
        Assert.Equal(0L, await RowsAsync(fixture, tokens[1]));
        Assert.Equal(1L, await RowsAsync(fixture, tokens[^1]));
    }

    [Fact]
    public async Task The_token_never_appears_in_logs()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var capture = new CapturingLoggerProvider();
        await using var factory = fixture.Derive(builder => builder.ConfigureLogging(logging => logging.AddProvider(capture)));
        var client = new CatalogClient(factory.CreateClient(), owner.TenantId, owner.UserId);
        client.Http.DefaultRequestHeaders.Authorization = owner.Http.DefaultRequestHeaders.Authorization;
        var token = NewToken();

        await RegisterAsync(client, token);
        await HttpAssert.ProblemAsync(await client.PostAsync("/api/v1/devices", new { token = token + " x", platform = "android" }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        Assert.NotEmpty(capture.Entries);
        Assert.All(capture.Entries, e =>
        {
            Assert.DoesNotContain(token, e.JsonLine, StringComparison.Ordinal);
            Assert.DoesNotContain(token, e.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Stale_registrations_are_purged_through_the_definer_function_only()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var stale = NewToken();
        var fresh = NewToken();
        await RegisterAsync(owner, stale);
        await RegisterAsync(owner, fresh);
        await using var superuser = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await superuser.OpenAsync(Ct);
        await using (var age = new NpgsqlCommand(
            "UPDATE notifications.device_tokens SET created_at = now() - interval '90 days', last_seen_at = now() - interval '61 days' WHERE token_sha256 = $1",
            superuser))
        {
            age.Parameters.Add(new NpgsqlParameter { Value = SHA256.HashData(Encoding.UTF8.GetBytes(stale)) });
            Assert.Equal(1, await age.ExecuteNonQueryAsync(Ct));
        }

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync(Ct);

        // Without a tenant the runtime role sees nothing; the function is the only way, and it refuses a short age.
        await using (var invisible = new NpgsqlCommand("SELECT count(*) FROM notifications.device_tokens", app))
        {
            Assert.Equal(0L, (long)(await invisible.ExecuteScalarAsync(Ct))!);
        }

        await using (var tooShort = new NpgsqlCommand("SELECT notifications.purge_stale_device_tokens(interval '7 days', 100)", app))
        {
            await Assert.ThrowsAsync<PostgresException>(() => tooShort.ExecuteScalarAsync(Ct));
        }

        await using (var purge = new NpgsqlCommand("SELECT notifications.purge_stale_device_tokens(interval '60 days', 10000)", app))
        {
            Assert.True((int)(await purge.ExecuteScalarAsync(Ct))! >= 1);
        }

        Assert.Equal(0L, await RowsAsync(fixture, stale));
        Assert.Equal(1L, await RowsAsync(fixture, fresh));
    }

    [Fact]
    public async Task Token_release_needs_a_request_context_and_is_not_public()
    {
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync(Ct);
        await using var release = new NpgsqlCommand("SELECT notifications.release_device_token($1)", app);
        release.Parameters.Add(new NpgsqlParameter { Value = SHA256.HashData("x"u8.ToArray()) });
        var error = await Assert.ThrowsAsync<PostgresException>(() => release.ExecuteScalarAsync(Ct));
        Assert.Contains("tenant and a user context", error.MessageText, StringComparison.Ordinal);

        await using var check = new NpgsqlCommand(
            """
            SELECT has_function_privilege('public', 'notifications.release_device_token(bytea)', 'EXECUTE'),
                   has_function_privilege('public', 'notifications.purge_stale_device_tokens(interval, integer)', 'EXECUTE'),
                   has_function_privilege('bayios_app', 'notifications.release_device_token(bytea)', 'EXECUTE')
            """,
            app);
        await using var reader = await check.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal((false, false, true), (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2)));
    }
}
