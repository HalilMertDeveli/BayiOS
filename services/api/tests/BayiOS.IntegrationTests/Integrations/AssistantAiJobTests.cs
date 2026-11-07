using System.Net;
using System.Text.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Platform;
using BayiOS.Modules.Integrations;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BayiOS.IntegrationTests.Integrations;

/// <summary>
/// The background AI parse (ADR 0010 §2.1, #121) with an offline Claude stand-in: the model's parse is used and
/// metered, provider errors and quota fall back to the rules parser, no connection is held during the provider call,
/// two workers never process the same parse, and a worker that lost its lease cannot commit a second result. These
/// hosts run with AI on, so the class runs alone (<see cref="GlobalWorkersTestGroup"/>): no AI-off worker of a parallel
/// test can pick up its jobs. Each test's stand-in answers only messages carrying the test's own marker.
/// </summary>
[Collection(GlobalWorkersTestGroup.Name)]
public sealed class AssistantAiJobTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> AiHost(FakeClaude claude, string? connectionString = null, int dailyCallLimit = 100) =>
        fixture.Derive(builder =>
        {
            builder.UseSetting("Ai:Enabled", "true");
            builder.UseSetting("Ai:DailyCallLimit", dailyCallLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Ai:BaseUrl", "https://claude.test");
            builder.UseSetting(AiOptions.ApiKeyKey, "test-key-not-a-real-secret");
            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:Default", connectionString);
            }

            builder.ConfigureTestServices(services =>
                services.AddHttpClient(IntegrationsModule.AnthropicHttpClient).ConfigurePrimaryHttpMessageHandler(() => claude));
        });

    private const string Line = "10 adet h4 ampul";

    /// <summary>
    /// A random lower-case word (no digits, nothing like a catalog word): the rules parser drops it as an unparsed
    /// fragment, so the message still has exactly one order line, and the stand-in recognises the test's requests by it.
    /// </summary>
    private static string Marker() =>
        "qx" + new string([.. Guid.NewGuid().ToByteArray().Take(10).Select(b => "qxzjvwk"[b % 7])]);

    private static string Message(string marker) => $"{Line}\nnot: {marker}";

    [Fact]
    public async Task Ai_path_parses_with_claude_records_usage_and_falls_back_on_quota_and_provider_errors()
    {
        var marker = Marker();
        var claude = new FakeClaude(marker)
            .Reply(FakeClaude.Output(FakeClaude.Line(Line, "h4 ampul", 10m, "adet")))
            .Fail();
        await using var host = AiHost(claude, dailyCallLimit: 2);
        var kit = await AssistantKit.CreateAsync(fixture);
        var owner = AssistantKit.On(host, kit.Owner);
        await AssistantKit.DrainAllAsync(fixture);
        var text = $"Ben Ahmet, 0532 123 45 67\n{Message(marker)}";

        // 1) The model's parse is used, validated and matched like any other.
        var first = await AssistantKit.SubmitOkAsync(host.Services, owner, text, kit.Orders.CustomerId);
        Assert.Equal("ai", first.Parser);
        Assert.Equal("claude-opus-5-5", first.Model);
        Assert.Null(first.FallbackReason);
        Assert.Equal(kit.Variants["MTR-1040"], Assert.Single(first.Lines).ProposedVariantId);
        Assert.Contains("personal_data_masked", first.Warnings);

        // Data minimisation: only the masked message text and the fixed prompt went to the provider.
        // The system block is the fixed, versioned prompt (its examples are generic); everything else is the messages
        // array, read JSON-decoded (the wire form escapes characters such as '+').
        var request = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(claude.Requests))!;
        Assert.Equal(BayiOS.Modules.Integrations.Parsing.AnthropicOrderParser.SystemPrompt, (string?)request["system"]![0]!["text"]);
        var sent = request["messages"]!.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Assert.Contains("+90 5** *** ** 67", sent, StringComparison.Ordinal);
        Assert.Contains("Ben [ad]", sent, StringComparison.Ordinal);  // person name masked (best effort, in context)
        Assert.DoesNotContain("Ahmet", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("123 45", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Sentetik Motor", sent, StringComparison.Ordinal); // customer name
        Assert.DoesNotContain("Debriyaj", sent, StringComparison.Ordinal);       // catalog
        Assert.DoesNotContain("MTR-10", sent, StringComparison.Ordinal);         // SKUs
        Assert.DoesNotContain(kit.Owner.TenantId.ToString(), sent, StringComparison.Ordinal);

        // 2) A provider error falls back to the rules parser with an explicit reason.
        var second = await AssistantKit.SubmitOkAsync(host.Services, owner, text);
        Assert.Equal("rules", second.Parser);
        Assert.Equal("provider_error", second.FallbackReason);
        Assert.Contains("ai_unavailable", second.Warnings);
        Assert.Equal(kit.Variants["MTR-1040"], Assert.Single(second.Lines).ProposedVariantId);

        // 3) The daily call quota (2) is used up: rules parser, no provider call at all.
        var third = await AssistantKit.SubmitOkAsync(host.Services, owner, text);
        Assert.Equal("quota_exceeded", third.FallbackReason);
        Assert.Contains("ai_quota_exceeded", third.Warnings);
        Assert.Equal(3, claude.Requests.Count); // call 1, call 2 + one SDK retry of the 500 (Ai:MaxRetries = 1); none for call 3

        var usage = await AssistantKit.ReadAsync<AiUsageResponse>(await owner.GetAsync("/api/v1/ai/usage"), HttpStatusCode.OK);
        Assert.True(usage.AiEnabled);
        Assert.Equal(3, usage.ParseRequests);
        Assert.Equal(2, usage.AiCalls);
        Assert.Equal(1, usage.AiFailures);
        Assert.Equal(2, usage.Fallbacks);
        Assert.Equal(220, usage.InputTokens);
        Assert.Equal(400, usage.OutputTokens);
        Assert.Equal(1650, usage.CacheReadTokens);
        Assert.Equal(620, usage.QuotaTokens);

        // The parse row keeps the call's tokens (cost per message) but the stored text is the masked one.
        Assert.Equal(1650, await AssistantKit.ScalarAsync<int>(fixture, "SELECT cache_read_tokens FROM integrations.ai_parse_requests WHERE id = $1", first.Id));
        var stored = await AssistantKit.ScalarAsync<string>(fixture, "SELECT raw_text FROM integrations.ai_parse_requests WHERE id = $1", first.Id);
        Assert.DoesNotContain("123 45", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Ahmet", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_database_connection_is_held_while_the_provider_is_called()
    {
        // A host whose every connection is a real server session (no pool) tagged with its own application name.
        var application = "bayios-ai-nohold-" + Guid.NewGuid().ToString("N")[..8];
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Pooling = false, ApplicationName = application }.ConnectionString;
        var marker = Marker();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claude = new FakeClaude(marker) { Gate = gate }
            .Reply(FakeClaude.Output(FakeClaude.Line(Line, "h4 ampul", 10m, "adet")));
        await using var host = AiHost(claude, connectionString);
        var kit = await AssistantKit.CreateAsync(fixture);
        var owner = AssistantKit.On(host, kit.Owner);
        await AssistantKit.DrainAllAsync(fixture);
        var accepted = await AssistantKit.ReadAsync<OrderParseAcceptedResponse>(await AssistantKit.SubmitAsync(owner, Message(marker)), HttpStatusCode.Accepted);

        // Control: a connection of the host's data source is visible under that name.
        await using (var probe = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            Assert.Equal(1L, await AssistantKit.ScalarAsync<long>(fixture, "SELECT count(*) FROM pg_stat_activity WHERE application_name = $1", application));
        }

        var processor = AssistantKit.Processor(host.Services, "nohold-worker", batchSize: 1);
        var run = Task.Run(() => processor.RunOnceAsync(Ct), Ct);
        await claude.Entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);

        // The provider call is in flight (and stays so until the gate opens): the worker's host has no session open at
        // all — none idle in transaction either. Closed sessions may take a moment to leave pg_stat_activity, a held one
        // never does.
        long sessions;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            sessions = await AssistantKit.ScalarAsync<long>(fixture, "SELECT count(*) FROM pg_stat_activity WHERE application_name = $1", application);
            if (sessions > 0)
            {
                await Task.Delay(50, Ct);
            }
        }
        while (sessions > 0 && DateTime.UtcNow < until);

        Assert.Equal(0L, sessions);
        Assert.False(run.IsCompleted);

        gate.SetResult();
        Assert.Equal(1, await run);
        var parse = await AssistantKit.ReadAsync<OrderParseResponse>(await owner.GetAsync(accepted.ResultUrl), HttpStatusCode.OK);
        Assert.Equal(("needs_review", "ai"), (parse.Status, parse.Parser));
    }

    [Fact]
    public async Task Two_workers_never_process_the_same_parse()
    {
        var marker = Marker();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claude = new FakeClaude(marker) { Gate = gate }
            .Reply(FakeClaude.Output(FakeClaude.Line(Line, "h4 ampul", 10m, "adet")))
            .Reply(FakeClaude.Output(FakeClaude.Line(Line, "h4 ampul", 10m, "adet")));
        await using var host = AiHost(claude);
        var kit = await AssistantKit.CreateAsync(fixture);
        var owner = AssistantKit.On(host, kit.Owner);
        await AssistantKit.DrainAllAsync(fixture);
        var accepted = await AssistantKit.ReadAsync<OrderParseAcceptedResponse>(await AssistantKit.SubmitAsync(owner, Message(marker)), HttpStatusCode.Accepted);

        var a = AssistantKit.Processor(host.Services, "race-a");
        var b = AssistantKit.Processor(host.Services, "race-b");
        var first = Task.Run(() => a.RunOnceAsync(Ct), Ct);
        await claude.Entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);

        // While A is waiting on the provider, B (and more polls) find nothing to claim: SKIP LOCKED + status running.
        var raced = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => b.RunOnceAsync(Ct), Ct)));
        Assert.All(raced, n => Assert.Equal(0, n));

        gate.SetResult();
        Assert.Equal(1, await first);
        Assert.Single(claude.Requests);
        Assert.Equal(1L, await AssistantKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'ai.parse_completed' AND entity_id = $2", kit.Owner.TenantId, accepted.Id));
        Assert.Equal("succeeded|1", await AssistantKit.ScalarAsync<string>(
            fixture,
            "SELECT status || '|' || attempts FROM platform.jobs WHERE tenant_id = $1 AND type = 'integrations.ai_order_parse'",
            kit.Owner.TenantId));
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_during_the_call_cannot_commit_a_second_result()
    {
        var marker = Marker();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claude = new FakeClaude(marker) { Gate = gate }
            .Reply(FakeClaude.Output(FakeClaude.Line(Line, "h4 ampul", 10m, "adet")))
            .Reply(FakeClaude.Output(FakeClaude.Line(Line, "h4 ampul", 10m, "adet")));
        await using var host = AiHost(claude);
        var kit = await AssistantKit.CreateAsync(fixture);
        var owner = AssistantKit.On(host, kit.Owner);
        await AssistantKit.DrainAllAsync(fixture);
        var accepted = await AssistantKit.ReadAsync<OrderParseAcceptedResponse>(await AssistantKit.SubmitAsync(owner, Message(marker)), HttpStatusCode.Accepted);

        // A claims the job and hangs in the provider call; its lease runs out (simulated) and B takes the job over.
        var slow = Task.Run(() => AssistantKit.Processor(host.Services, "lease-a").RunOnceAsync(Ct), Ct);
        await claude.Entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
        await AssistantKit.ExecuteAsync(
            fixture,
            "UPDATE platform.jobs SET locked_until = now() - interval '1 second' WHERE tenant_id = $1 AND type = 'integrations.ai_order_parse'",
            kit.Owner.TenantId);
        Assert.Equal(1, await AssistantKit.Processor(host.Services, "lease-b").RunOnceAsync(Ct));
        var done = await AssistantKit.ReadAsync<OrderParseResponse>(await owner.GetAsync(accepted.ResultUrl), HttpStatusCode.OK);
        Assert.Equal("needs_review", done.Status);

        // A wakes up: its result is not committed (the parse is no longer pending and the lease is B's).
        gate.SetResult();
        await slow;
        Assert.Equal(2, claude.Requests.Count);
        Assert.Equal(1L, await AssistantKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'ai.parse_completed' AND entity_id = $2", kit.Owner.TenantId, accepted.Id));
        var after = await AssistantKit.ReadAsync<OrderParseResponse>(await owner.GetAsync(accepted.ResultUrl), HttpStatusCode.OK);
        Assert.Equal(done.Version, after.Version);
        Assert.Equal("succeeded", await AssistantKit.ScalarAsync<string>(
            fixture, "SELECT status FROM platform.jobs WHERE tenant_id = $1 AND type = 'integrations.ai_order_parse'", kit.Owner.TenantId));
    }
}
