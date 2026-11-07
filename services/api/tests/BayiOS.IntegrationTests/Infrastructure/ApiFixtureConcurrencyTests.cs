using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BayiOS.IntegrationTests.Infrastructure;

/// <summary>
/// #153 regression: the shared fixture is used from many parallel test collections at once. Before the fix, concurrent
/// CreateClient/WithWebHostBuilder calls corrupted WebApplicationFactory's private List&lt;T&gt;s (lost item / null slot)
/// and the assembly fixture threw NullReferenceException in DisposeAsync. This test hammers both entry points from
/// many threads and checks, through reflection, that every created client and derived factory is tracked exactly once
/// and that no slot is null — i.e. that DisposeAsync will see a consistent list.
/// </summary>
public class ApiFixtureConcurrencyTests(ApiFixture fixture)
{
    private const int Threads = 32;
    private const int ClientsPerThread = 40;
    private const int DerivedPerThread = 4;

    [Fact]
    public async Task Concurrent_client_and_host_creation_keeps_the_factory_lists_consistent()
    {
        var clients = TrackedList("_clients");
        var derived = TrackedList("_derivedFactories");

        var createdClients = new System.Collections.Concurrent.ConcurrentBag<HttpClient>();
        var createdHosts = new System.Collections.Concurrent.ConcurrentBag<WebApplicationFactory<Program>>();
        using var start = new Barrier(Threads);

        var workers = Enumerable.Range(0, Threads).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (var i = 0; i < ClientsPerThread; i++)
                {
                    createdClients.Add(fixture.CreateClient());
                    if (i % (ClientsPerThread / DerivedPerThread) == 0)
                    {
                        // Never started: construction alone is what registers it with the parent.
                        createdHosts.Add(fixture.Derive(_ => { }));
                    }
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers);

        Assert.Equal(Threads * ClientsPerThread, createdClients.Count);
        Assert.Equal(Threads * DerivedPerThread, createdHosts.Count);

        IReadOnlyList<object?> clientSnapshot;
        IReadOnlyList<object?> derivedSnapshot;
        lock (fixture.Gate) // other test classes keep creating clients in parallel
        {
            clientSnapshot = clients.Cast<object?>().ToList();
            derivedSnapshot = derived.Cast<object?>().ToList();
        }

        Assert.DoesNotContain(null, clientSnapshot);
        Assert.DoesNotContain(null, derivedSnapshot);
        var trackedClients = clientSnapshot.ToHashSet(ReferenceEqualityComparer.Instance);
        var trackedHosts = derivedSnapshot.ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.All(createdClients, c => Assert.Contains(c, trackedClients));
        Assert.All(createdHosts, h => Assert.Contains(h, trackedHosts));

        foreach (var client in createdClients)
        {
            client.Dispose();
        }

        foreach (var host in createdHosts)
        {
            await host.DisposeAsync();
        }
    }

    private IList TrackedList(string field) =>
        (IList)(typeof(WebApplicationFactory<Program>).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"WebApplicationFactory<T>.{field} not found — Mvc.Testing changed; re-check the #153 fixture gate."))
        .GetValue(fixture)!;
}
