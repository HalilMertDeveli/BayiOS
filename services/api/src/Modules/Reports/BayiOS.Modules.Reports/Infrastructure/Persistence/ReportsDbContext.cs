using System.Data.Common;
using BayiOS.Modules.Reports.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Reports.Infrastructure.Persistence;

/// <summary>
/// The <c>reports</c> schema: only the export log behind the hourly export limit. Report data itself is read through
/// the ports (other modules' schemas are never queried from here).
/// </summary>
internal sealed class ReportsDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "reports";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public ReportsDbContext(DbContextOptions<ReportsDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<ExportRequestRecord> ExportRequests => Set<ExportRequestRecord>();

    public Guid? CurrentTenantId => _requestContext.TenantId;

    public static void Configure(DbContextOptionsBuilder builder, DbConnection connection)
    {
        builder.UseNpgsql(connection, ConfigureNpgsql);
        Finish(builder);
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, ConfigureNpgsql);
        Finish(builder);
    }

    /// <summary>Joins the request transaction (nested transactions are not supported).</summary>
    public async Task JoinTransactionAsync(CancellationToken cancellationToken)
    {
        if (_unitOfWork?.Transaction is { } transaction && Database.CurrentTransaction is null)
        {
            await Database.UseTransactionAsync(transaction, cancellationToken);
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await JoinTransactionAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync so the request transaction is joined.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ExportRequestRecord>(export =>
        {
            export.ToTable("export_requests", t =>
            {
                t.HasCheckConstraint("ck_export_requests_report", $"report IN ({Quoted(ReportKinds.All)})");
                t.HasCheckConstraint("ck_export_requests_format", $"format IN ({Quoted(ExportFormats.All)})");
                t.HasCheckConstraint("ck_export_requests_row_count", "row_count >= 0");
            });
            export.HasKey(e => e.Id);
            export.Property(e => e.Id).ValueGeneratedNever();
            export.Property(e => e.Report).IsRequired();
            export.Property(e => e.Format).IsRequired();
            export.HasIndex(e => new { e.TenantId, e.CreatedAt }).IsDescending(false, true);
            export.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);

    private static void Finish(DbContextOptionsBuilder builder) =>
        builder.UseSnakeCaseNamingConvention().AddInterceptors(TenantGuard);
}

/// <summary>
/// For <c>dotnet ef</c>. Applying migrations uses the schema-owner role: set <c>ConnectionStrings__Migrator</c>
/// (never the runtime <c>bayios_app</c> connection). Adding migrations needs no database.
/// </summary>
internal sealed class ReportsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReportsDbContext>
{
    public ReportsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<ReportsDbContext>();
        ReportsDbContext.Configure(builder, connectionString);
        return new ReportsDbContext(builder.Options, new RequestContext());
    }
}
