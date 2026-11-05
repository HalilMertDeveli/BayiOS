namespace BayiOS.IntegrationTests.Platform;

/// <summary>
/// Tests that run a global job processor, outbox dispatcher or hosted worker against the shared database. They run
/// alone (after the parallel tests): their workers claim every tenant's work, and another test's processor (e.g. the
/// imports tests) could otherwise claim the jobs these tests assert on.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalWorkersTestGroup
{
    public const string Name = "GlobalWorkers";
}
