namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// Tests that compare a tenant's complete database snapshot before and after an action. They run alone (after the
/// parallel tests), so no concurrent test can touch the snapshot — e.g. a global outbox dispatcher or job processor.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenantSnapshotTestGroup
{
    public const string Name = "TenantSnapshot";
}
