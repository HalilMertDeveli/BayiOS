namespace BayiOS.IntegrationTests.BulkImport;

/// <summary>
/// Import test classes drive the shared job processor, which claims every queued job type. Some tests count chunks
/// with their own processor (e.g. cancel mid-run), so the import classes run one after another in one collection:
/// a processor of one class never runs another class's import chunk in between.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ImportJobsTestGroup
{
    public const string Name = "ImportJobs";
}
