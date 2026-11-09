using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// #54 security audit (docs/security/audit-2026-11.md): verification tests for threat-model items that had no
/// automated test yet. Each test names the threat-model row it proves.
/// </summary>
[Trait("Category", "Security")]
public sealed class SecurityAuditGapTests(ApiFixture fixture)
{
    private const string DevWebOrigin = "http://localhost:3200"; // appsettings.Development.json (test host = Development)

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Threat model §3.3 T "gövdede tenant_id": no request body schema in the running API's OpenAPI has a tenant field.</summary>
    [Fact]
    public async Task No_request_body_schema_accepts_a_tenant_id()
    {
        var document = JsonNode.Parse(await fixture.CreateClient().GetStringAsync("/openapi/v1.json", Ct))!;
        var schemas = document["components"]!["schemas"]!.AsObject();
        var offenders = new List<string>();
        var bodies = 0;
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (operation?["requestBody"]?["content"] is not JsonObject content)
                {
                    continue;
                }

                foreach (var (mediaType, media) in content)
                {
                    bodies++;
                    foreach (var property in PropertyNames(media?["schema"], schemas, []))
                    {
                        if (property.Contains("tenant", StringComparison.OrdinalIgnoreCase))
                        {
                            offenders.Add($"{method.ToUpperInvariant()} {path} ({mediaType}): {property}");
                        }
                    }
                }
            }
        }

        Assert.True(bodies > 40, $"Only {bodies} request bodies found — the OpenAPI walk is broken.");
        Assert.True(offenders.Count == 0, "Request bodies must never carry a tenant: " + string.Join("; ", offenders));
    }

    /// <summary>§3.3 / §6: CORS answers only the configured web origin — a foreign origin gets no ACAO header, also on preflight.</summary>
    [Fact]
    public async Task Cors_never_allows_a_foreign_origin()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        foreach (var origin in new[] { "https://evil.example", "http://localhost:3200.evil.example", "null" })
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant");
            get.Headers.Add("Origin", origin);
            get.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
            var response = await owner.Http.SendAsync(get, Ct);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), $"GET from {origin} got an ACAO header");
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"), $"GET from {origin} got ACAC");

            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/orders");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization,x-tenant-id,idempotency-key,content-type");
            var answer = await fixture.CreateClient().SendAsync(preflight, Ct);
            Assert.False(answer.Headers.Contains("Access-Control-Allow-Origin"), $"preflight from {origin} was allowed");
        }

        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/orders");
        allowed.Headers.Add("Origin", DevWebOrigin);
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        allowed.Headers.Add("Access-Control-Request-Headers", "authorization,x-tenant-id");
        var ok = await fixture.CreateClient().SendAsync(allowed, Ct);
        Assert.Equal(DevWebOrigin, Assert.Single(ok.Headers.GetValues("Access-Control-Allow-Origin")));
        // Bearer tokens, no cookies: credentials are never allowed cross-origin.
        Assert.False(ok.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    /// <summary>T3 başlık sahteciliği: a second X-Tenant-Id (or a list) cannot smuggle another tenant past the membership check.</summary>
    [Fact]
    public async Task Duplicate_or_listed_tenant_headers_are_rejected_not_resolved()
    {
        var victim = await CatalogClient.NewTenantOwnerAsync(fixture);
        var attacker = await CatalogClient.NewTenantOwnerAsync(fixture);

        using (var twice = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant"))
        {
            twice.Headers.TryAddWithoutValidation("X-Tenant-Id", attacker.TenantId.ToString());
            twice.Headers.TryAddWithoutValidation("X-Tenant-Id", victim.TenantId.ToString());
            await HttpAssert.ProblemAsync(await attacker.Http.SendAsync(twice, Ct), HttpStatusCode.BadRequest, "request.malformed");
        }

        foreach (var value in new[]
        {
            $"{attacker.TenantId},{victim.TenantId}",
            $"{victim.TenantId}, {attacker.TenantId}",
            $" {victim.TenantId}\t",
            "{" + victim.TenantId + "}",
            victim.TenantId.ToString("N"),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant");
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", value);
            var response = await attacker.Http.SendAsync(request, Ct);
            // Either malformed or an ordinary membership refusal — never the victim's data.
            Assert.True(
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
                $"X-Tenant-Id '{value}' -> {(int)response.StatusCode}");
            Assert.DoesNotContain(victim.TenantId.ToString(), await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>§5 JWT: shapes not in JwtValidationTests — no exp, non-UUID sub, RS256 public key reused as an HS256 secret.</summary>
    [Fact]
    public async Task Tokens_without_expiry_with_a_non_uuid_subject_or_alg_confusion_are_rejected()
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var now = DateTime.UtcNow;
        string Signed(Dictionary<string, object> claims, DateTime? expires, SigningCredentials credentials) => handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = expires,
            SigningCredentials = credentials,
        });
        var rs256 = new SigningCredentials(TestTokens.SigningKey, SecurityAlgorithms.RsaSha256);
        var publicJwk = JsonDocument.Parse(TestTokens.JwksJson).RootElement.GetProperty("keys")[0];
        var publicKeyBytes = Base64UrlEncoder.DecodeBytes(publicJwk.GetProperty("n").GetString()!);
        var confused = new SigningCredentials(new SymmetricSecurityKey(publicKeyBytes) { KeyId = TestTokens.KeyId }, SecurityAlgorithms.HmacSha256);
        var jwksText = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestTokens.JwksJson)) { KeyId = TestTokens.KeyId }, SecurityAlgorithms.HmacSha256);
        var sub = Guid.NewGuid().ToString();

        var cases = new Dictionary<string, string>
        {
            ["no exp"] = Signed(new() { ["sub"] = sub, ["role"] = "authenticated" }, null, rs256),
            ["non-uuid sub"] = Signed(new() { ["sub"] = "admin", ["role"] = "authenticated" }, now.AddMinutes(5), rs256),
            ["empty sub"] = Signed(new() { ["sub"] = string.Empty, ["role"] = "authenticated" }, now.AddMinutes(5), rs256),
            ["HS256 keyed with the RSA modulus"] = Signed(new() { ["sub"] = sub, ["role"] = "authenticated" }, now.AddMinutes(5), confused),
            ["HS256 keyed with the JWKS document"] = Signed(new() { ["sub"] = sub, ["role"] = "authenticated" }, now.AddMinutes(5), jwksText),
            ["service_role audience"] = TestTokens.Create(Guid.NewGuid(), audience: "service_role"),
        };

        foreach (var (reason, token) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            request.Headers.Authorization = new("Bearer", token);
            var response = await fixture.CreateClient().SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{reason}: {(int)response.StatusCode}");
        }
    }

    /// <summary>§3.3 I "hata mesajında stack trace / SQL": in Production a crash is a bare ProblemDetails; no OpenAPI document.</summary>
    [Fact]
    public async Task Production_hides_exception_details_and_the_openapi_document()
    {
        await using var production = fixture.Derive(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMembershipLookup>();
                services.AddScoped<IMembershipLookup, ExplodingMembershipLookup>();
            });
        });
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var http = production.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant");
        request.Headers.Authorization = owner.Http.DefaultRequestHeaders.Authorization;
        request.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());

        var response = await http.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        foreach (var leak in new[] { ExplodingMembershipLookup.Secret, "InvalidOperationException", "ExplodingMembershipLookup", "   at ", "BayiOS.", "Npgsql" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/openapi/v1.json", Ct)).StatusCode);
    }

    /// <summary>
    /// §3.3 D: every paged list refuses an oversized, zero, negative or non-integer page size (no unbounded reads, no
    /// 500). Run against a Production host: in Development, minimal-API binding failures are thrown on purpose
    /// (RouteHandlerOptions.ThrowOnBadRequest) and surface as 500 on routes that bind <c>int?</c> (ai/order-parses).
    /// </summary>
    [Fact]
    public async Task Every_paged_list_rejects_out_of_range_limits_in_production()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var document = JsonNode.Parse(await fixture.CreateClient().GetStringAsync("/openapi/v1.json", Ct))!;
        await using var production = fixture.Derive(builder => builder.UseEnvironment("Production"));
        var http = production.CreateClient();
        http.DefaultRequestHeaders.Authorization = owner.Http.DefaultRequestHeaders.Authorization;
        owner = new CatalogClient(http, owner.TenantId, owner.UserId);
        var routes = document["paths"]!.AsObject()
            .Where(p => p.Value?["get"]?["parameters"] is JsonArray parameters && parameters.Any(x => (string?)x?["name"] == "limit"))
            .Select(p => p.Key)
            .ToList();
        Assert.True(routes.Count >= 15, $"Only {routes.Count} paged routes found.");

        var failures = new List<string>();
        foreach (var route in routes)
        {
            var path = route.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
            foreach (var limit in new[] { "100000", "0", "-1", "1e3" })
            {
                var response = await owner.SendAsync(HttpMethod.Get, $"{path}?limit={limit}");
                var allowed = route.Contains('{', StringComparison.Ordinal)
                    ? response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound
                    : response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
                if (!allowed)
                {
                    failures.Add($"GET {route}?limit={limit} -> {(int)response.StatusCode}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>T2 eksik filtre, every module at once: each entity with a TenantId has a global query filter (RLS stays the second layer).</summary>
    [Fact]
    public async Task Every_tenant_owned_entity_in_every_module_has_a_query_filter()
    {
        // Queue/outbox/idempotency/audit rows are read by platform code by design (data-model §2.3, threat model §3.6);
        // they are protected by RLS or the tenant trigger (DatabaseSecurityGuardTests), not by an EF filter.
        var platformOnly = new HashSet<string>(StringComparer.Ordinal) { "PlatformDbContext" };
        await using var scope = fixture.Services.CreateAsyncScope();
        var contextTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("BayiOS.", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false } && typeof(DbContext).IsAssignableFrom(t))
            .ToList();
        var checkedContexts = new List<string>();
        var missing = new List<string>();
        foreach (var type in contextTypes.Where(t => !platformOnly.Contains(t.Name)))
        {
            if (scope.ServiceProvider.GetService(type) is not DbContext context)
            {
                continue;
            }

            checkedContexts.Add(type.Name);
            missing.AddRange(context.Model.GetEntityTypes()
                .Where(e => !e.IsOwned() && e.FindProperty("TenantId") is not null && e.GetDeclaredQueryFilters().Count == 0 && e.BaseType is null)
                .Select(e => $"{type.Name}.{e.ClrType.Name}"));
        }

        Assert.True(checkedContexts.Count >= 11, "Module DbContexts resolved: " + string.Join(", ", checkedContexts));
        Assert.True(missing.Count == 0, "Tenant entities without a query filter: " + string.Join(", ", missing));
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<string> PropertyNames(JsonNode? schema, JsonObject schemas, HashSet<string> seen)
    {
        if (schema is not JsonObject node)
        {
            yield break;
        }

        if ((string?)node["$ref"] is { } reference)
        {
            var name = reference[(reference.LastIndexOf('/') + 1)..];
            if (seen.Add(name))
            {
                foreach (var p in PropertyNames(schemas[name], schemas, seen))
                {
                    yield return p;
                }
            }

            yield break;
        }

        if (node["properties"] is JsonObject properties)
        {
            foreach (var (name, value) in properties)
            {
                yield return name;
                foreach (var p in PropertyNames(value, schemas, seen))
                {
                    yield return $"{name}.{p}";
                }
            }
        }

        foreach (var key in new[] { "items", "additionalProperties" })
        {
            foreach (var p in PropertyNames(node[key], schemas, seen))
            {
                yield return p;
            }
        }

        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (node[key] is JsonArray variants)
            {
                foreach (var variant in variants)
                {
                    foreach (var p in PropertyNames(variant, schemas, seen))
                    {
                        yield return p;
                    }
                }
            }
        }
    }

    private sealed class ExplodingMembershipLookup : IMembershipLookup
    {
        public const string Secret = "Host=db.internal;Password=hunter2-not-real;SELECT secret FROM identity.memberships";

        public Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<TenantAccess?> FindAccessAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);
    }
}
