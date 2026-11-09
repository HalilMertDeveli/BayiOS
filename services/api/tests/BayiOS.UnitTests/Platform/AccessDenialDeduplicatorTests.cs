using System.Text.Json;
using BayiOS.Api.Platform.Audit;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace BayiOS.UnitTests.Platform;

/// <summary>#89: the access-denial dedupe bounds database writes per (user, tenant, code) and overall.</summary>
public class AccessDenialDeduplicatorTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public void One_write_per_key_and_window_then_the_repeats_ride_on_the_next_row()
    {
        var clock = new ManualClock();
        var dedupe = new AccessDenialDeduplicator(Window, 100, clock);
        var key = Key("auth.forbidden");

        Assert.Equal(DenialDecision.Write, dedupe.Decide(key, out var repeats));
        Assert.Equal(0, repeats);
        for (var i = 0; i < 7; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(DenialDecision.Suppressed, dedupe.Decide(key, out _));
        }

        clock.Advance(TimeSpan.FromSeconds(25)); // 60 s after the written row
        Assert.Equal(DenialDecision.Write, dedupe.Decide(key, out repeats));
        Assert.Equal(7, repeats);
        Assert.Equal(DenialDecision.Suppressed, dedupe.Decide(key, out _));
    }

    [Fact]
    public void User_tenant_and_code_each_make_a_separate_key()
    {
        var dedupe = new AccessDenialDeduplicator(Window, 100, new ManualClock());
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        Assert.Equal(DenialDecision.Write, dedupe.Decide(new DenialKey(user, tenant, "auth.forbidden"), out _));
        Assert.Equal(DenialDecision.Write, dedupe.Decide(new DenialKey(user, tenant, "orders.not_owner"), out _));
        Assert.Equal(DenialDecision.Write, dedupe.Decide(new DenialKey(user, Guid.NewGuid(), "auth.forbidden"), out _));
        Assert.Equal(DenialDecision.Write, dedupe.Decide(new DenialKey(Guid.NewGuid(), tenant, "auth.forbidden"), out _));
        Assert.Equal(DenialDecision.Suppressed, dedupe.Decide(new DenialKey(user, tenant, "auth.forbidden"), out _));
    }

    [Fact]
    public void A_full_table_drops_new_keys_until_old_ones_expire()
    {
        var clock = new ManualClock();
        var dedupe = new AccessDenialDeduplicator(Window, 3, clock);
        var known = Key("auth.forbidden");
        Assert.Equal(DenialDecision.Write, dedupe.Decide(known, out _));
        Assert.Equal(DenialDecision.Write, dedupe.Decide(Key("auth.forbidden"), out _));
        Assert.Equal(DenialDecision.Write, dedupe.Decide(Key("auth.forbidden"), out _));

        // A flood of fresh keys (e.g. rotated tenant ids) cannot grow the table or the writes.
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(DenialDecision.Dropped, dedupe.Decide(Key("tenant.access_denied"), out _));
        }

        Assert.Equal(3, dedupe.Count);
        Assert.Equal(DenialDecision.Suppressed, dedupe.Decide(known, out _)); // live keys keep their dedupe

        clock.Advance(Window);
        Assert.Equal(DenialDecision.Write, dedupe.Decide(Key("tenant.access_denied"), out _));
        Assert.Equal(1, dedupe.Count);
    }

    [Fact]
    public void A_full_table_is_swept_at_most_once_per_tenth_of_a_window()
    {
        var clock = new ManualClock();
        var dedupe = new AccessDenialDeduplicator(Window, 2, clock);
        Assert.Equal(DenialDecision.Write, dedupe.Decide(Key("a.b"), out _));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(DenialDecision.Write, dedupe.Decide(Key("a.b"), out _));
        Assert.Equal(DenialDecision.Dropped, dedupe.Decide(Key("a.b"), out _)); // sweep: nothing expired yet

        clock.Advance(TimeSpan.FromSeconds(31)); // the first key has expired, but the last sweep was 31 s ago (> 6 s)
        Assert.Equal(DenialDecision.Write, dedupe.Decide(Key("a.b"), out _));
        Assert.Equal(DenialDecision.Dropped, dedupe.Decide(Key("a.b"), out _)); // full again, swept just now
    }

    [Fact]
    public void A_failed_write_is_retried_by_the_next_denial_and_counted()
    {
        var clock = new ManualClock();
        var dedupe = new AccessDenialDeduplicator(Window, 10, clock);
        var key = Key("auth.forbidden");
        Assert.Equal(DenialDecision.Write, dedupe.Decide(key, out var repeats));

        dedupe.Forget(key, repeats);
        Assert.Equal(DenialDecision.Write, dedupe.Decide(key, out repeats));
        Assert.Equal(1, repeats); // the lost denial
        Assert.Equal(DenialDecision.Suppressed, dedupe.Decide(key, out _));
    }

    [Fact]
    public void Changes_hold_server_values_only()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "PATCH";
        http.Request.Path = "/api/v1/orders/0b6f3c55-0000-0000-0000-000000000000";
        http.Request.QueryString = new QueryString("?q=secret");
        http.Request.Headers.UserAgent = "Mozilla/5.0 (kişisel)";
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/orders/{id}"), 0, EndpointMetadataCollection.Empty, "test"));

        var json = AccessDenialAuditLog.Changes(
            http, new AccessDenial(AccessDenialActions.AccessDenied, "orders.not_owner", Guid.NewGuid(), Roles.Sales, [Permissions.OrdersEditDraft]), 4);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("code,method,route,permissions,suppressedRepeats", string.Join(',', root.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(("orders.not_owner", "PATCH", "/api/v1/orders/{id}", 4), (
            root.GetProperty("code").GetString(), root.GetProperty("method").GetString(), root.GetProperty("route").GetString(),
            root.GetProperty("suppressedRepeats").GetInt32()));
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0b6f3c55", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Mozilla", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_are_unwrapped_from_endpoint_results()
    {
        var problem = ApiProblems.Create(StatusCodes.Status403Forbidden, "orders.not_owner", "Not allowed");
        Results<Ok, ProblemHttpResult> union = TypedResults.Problem(problem);

        Assert.Same(problem, AccessDenials.ProblemOf(union));
        Assert.Same(problem, AccessDenials.ProblemOf(TypedResults.Problem(problem)));
        Assert.Null(AccessDenials.ProblemOf(TypedResults.Forbid()));
    }

    private static DenialKey Key(string code) => new(Guid.NewGuid(), Guid.NewGuid(), code);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
