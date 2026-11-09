using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Reports.Domain;

/// <summary>
/// reports.export_requests — one row per produced export, only for the per-tenant hourly export limit. Holds no
/// report data and no person (the actor is in the audit event); rows older than a day are deleted by the next export.
/// </summary>
internal sealed class ExportRequestRecord : ITenantOwned
{
    private ExportRequestRecord()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public string Report { get; private init; } = string.Empty;

    public string Format { get; private init; } = string.Empty;

    public int RowCount { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ExportRequestRecord Create(Guid tenantId, string report, string format, int rowCount, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Report = report,
        Format = format,
        RowCount = rowCount,
        CreatedAt = now,
    };
}
