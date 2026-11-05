namespace BayiOS.IntegrationTests.Security;

/// <summary>The snapshot diff names every inserted, deleted and updated row and the exact columns that changed.</summary>
public sealed class TenantSnapshotTests
{
    [Fact]
    public void Diff_reports_rows_and_changed_columns()
    {
        var before = new TenantSnapshot(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["platform.outbox_messages"] = new Dictionary<string, string>
            {
                ["m1"] = """{"id": "m1", "attempts": 0, "processed_at": null, "payload": {"n": 1}}""",
                ["m2"] = """{"id": "m2", "attempts": 0}""",
            },
            ["catalog.units"] = new Dictionary<string, string> { ["u1"] = """{"id": "u1", "name": "Adet"}""" },
        });
        var after = new TenantSnapshot(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["platform.outbox_messages"] = new Dictionary<string, string>
            {
                ["m1"] = """{"id": "m1", "attempts": 1, "processed_at": "2026-11-05T06:10:50+00:00", "payload": {"n": 1}}""",
                ["m3"] = """{"id": "m3", "attempts": 0}""",
            },
            ["catalog.units"] = new Dictionary<string, string> { ["u1"] = """{"id": "u1", "name": "Adet"}""" },
        });

        Assert.Equal(
            [
                "platform.outbox_messages [m2] deleted: {\"id\": \"m2\", \"attempts\": 0}",
                "platform.outbox_messages [m3] inserted: {\"id\": \"m3\", \"attempts\": 0}",
                "platform.outbox_messages [m1] updated: attempts: 0 → 1; processed_at: null → \"2026-11-05T06:10:50+00:00\"",
            ],
            before.Diff(after));
        Assert.Empty(before.Diff(before));
    }
}
