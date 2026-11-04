using System.Data.Common;
using BayiOS.Modules.Imports.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Imports.Infrastructure.Persistence;

internal sealed class ImportsDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "imports";

    public const string TemplateNameUniqueIndex = "ux_mapping_templates_tenant_id_type_name";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public ImportsDbContext(DbContextOptions<ImportsDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<ImportJob> Jobs => Set<ImportJob>();

    public DbSet<ImportFile> Files => Set<ImportFile>();

    public DbSet<ImportRow> Rows => Set<ImportRow>();

    public DbSet<MappingTemplate> Templates => Set<MappingTemplate>();

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

    /// <summary>Joins the request/job transaction (nested transactions are not supported).</summary>
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

        modelBuilder.Entity<ImportJob>(job =>
        {
            job.ToTable("import_jobs", t =>
            {
                t.HasCheckConstraint("ck_import_jobs_type", $"type IN ({Quoted(ImportTypes.All)})");
                t.HasCheckConstraint("ck_import_jobs_status", $"status IN ({Quoted(ImportStatuses.All)})");
                t.HasCheckConstraint("ck_import_jobs_mode", $"mode IN ({Quoted(ImportModes.All)})");
                t.HasCheckConstraint("ck_import_jobs_match_key", $"match_key IN ({Quoted(MatchKeys.All)})");
                t.HasCheckConstraint("ck_import_jobs_file_format", "file_format IN ('xlsx', 'csv')");
                t.HasCheckConstraint("ck_import_jobs_file_sha256", "file_sha256 ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_import_jobs_counters", "total_rows >= 0 AND processed_rows >= 0 AND checkpoint_row >= 0");
            });
            job.HasKey(j => j.Id);
            job.Property(j => j.Id).ValueGeneratedNever();
            job.HasAlternateKey(j => new { j.TenantId, j.Id });
            job.Property(j => j.RowVersion).IsConcurrencyToken();
            job.Property(j => j.Type).IsRequired();
            job.Property(j => j.Status).IsRequired();
            job.Property(j => j.FileName).IsRequired();
            job.Property(j => j.FileSha256).IsRequired().HasColumnType("char(64)");
            job.Property(j => j.FileFormat).IsRequired();
            job.Property(j => j.Headers).IsRequired().HasColumnType("jsonb");
            job.Property(j => j.SampleRows).IsRequired().HasColumnType("jsonb");
            job.Property(j => j.Mapping).HasColumnType("jsonb");
            job.Property(j => j.MatchKey).IsRequired();
            job.Property(j => j.Mode).IsRequired();
            job.Ignore(j => j.PlannedRows);
            job.HasIndex(j => new { j.TenantId, j.CreatedAt }).IsDescending(false, true);
            // PRD IMP-09: "this file was already imported" warning lookup.
            job.HasIndex(j => new { j.TenantId, j.FileSha256, j.Type });
            job.HasQueryFilter(j => j.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ImportFile>(file =>
        {
            file.ToTable("import_files");
            file.HasKey(f => f.ImportJobId);
            file.Property(f => f.Content).IsRequired().HasColumnType("bytea");
            file.HasOne<ImportJob>().WithOne()
                .HasForeignKey<ImportFile>(f => new { f.TenantId, f.ImportJobId })
                .HasPrincipalKey<ImportJob>(j => new { j.TenantId, j.Id })
                .OnDelete(DeleteBehavior.Cascade);
            file.HasIndex(f => new { f.TenantId, f.ImportJobId }).IsUnique();
            file.HasQueryFilter(f => f.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ImportRow>(row =>
        {
            row.ToTable("import_rows", t =>
            {
                t.HasCheckConstraint("ck_import_rows_action", $"action IN ({Quoted(RowActions.All)})");
                t.HasCheckConstraint("ck_import_rows_status", $"status IN ({Quoted(RowStatuses.All)})");
                t.HasCheckConstraint("ck_import_rows_row_number", "row_number >= 2");
            });
            row.HasKey(r => r.Id);
            row.Property(r => r.Id).ValueGeneratedNever();
            row.Property(r => r.Raw).IsRequired().HasColumnType("jsonb");
            row.Property(r => r.Normalized).IsRequired().HasColumnType("jsonb");
            row.Property(r => r.Errors).IsRequired().HasColumnType("jsonb");
            row.Property(r => r.PreviousValues).HasColumnType("jsonb");
            row.Property(r => r.Action).IsRequired();
            row.Property(r => r.Status).IsRequired();
            row.HasOne<ImportJob>().WithMany()
                .HasForeignKey(r => new { r.TenantId, r.ImportJobId })
                .HasPrincipalKey(j => new { j.TenantId, j.Id })
                .OnDelete(DeleteBehavior.Cascade);
            row.HasIndex(r => new { r.TenantId, r.ImportJobId, r.RowNumber }).IsUnique();
            // Chunk claim (status = valid ordered by row number) and the error filter.
            row.HasIndex(r => new { r.TenantId, r.ImportJobId, r.Status, r.RowNumber });
            row.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MappingTemplate>(template =>
        {
            template.ToTable("mapping_templates", t =>
            {
                t.HasCheckConstraint("ck_mapping_templates_type", $"type IN ({Quoted(ImportTypes.All)})");
                t.HasCheckConstraint("ck_mapping_templates_mode", $"mode IN ({Quoted(ImportModes.All)})");
                t.HasCheckConstraint("ck_mapping_templates_match_key", $"match_key IN ({Quoted(MatchKeys.All)})");
            });
            template.HasKey(t => t.Id);
            template.Property(t => t.Id).ValueGeneratedNever();
            template.HasAlternateKey(t => new { t.TenantId, t.Id });
            template.Property(t => t.RowVersion).IsConcurrencyToken();
            template.Property(t => t.Name).IsRequired();
            template.Property(t => t.Type).IsRequired();
            template.Property(t => t.Mapping).IsRequired().HasColumnType("jsonb");
            template.Property(t => t.MatchKey).IsRequired();
            template.Property(t => t.Mode).IsRequired();
            template.HasIndex(t => new { t.TenantId, t.Type, t.Name }).IsUnique().HasDatabaseName(TemplateNameUniqueIndex);
            template.HasQueryFilter(t => t.TenantId == CurrentTenantId);
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
internal sealed class ImportsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ImportsDbContext>
{
    public ImportsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<ImportsDbContext>();
        ImportsDbContext.Configure(builder, connectionString);
        return new ImportsDbContext(builder.Options, new RequestContext());
    }
}
