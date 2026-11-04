using System.Data.Common;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WarehouseModel = BayiOS.Modules.Warehouse.Domain.Warehouse;

namespace BayiOS.Modules.Warehouse.Infrastructure.Persistence;

internal sealed class WarehouseDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "warehouse";

    // Index names the application maps to stable problem codes.
    public const string CodeUniqueIndex = "ux_warehouses_tenant_id_code";
    public const string DefaultUniqueIndex = "ux_warehouses_tenant_id_default";
    public const string LocationCodeUniqueIndex = "ux_warehouse_locations_tenant_id_warehouse_id_code";
    public const string LocationBarcodeUniqueIndex = "ux_warehouse_locations_tenant_id_barcode";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public WarehouseDbContext(DbContextOptions<WarehouseDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<WarehouseModel> Warehouses => Set<WarehouseModel>();

    public DbSet<WarehouseLocation> Locations => Set<WarehouseLocation>();

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

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Join the request transaction so EF does not try to open its own (nested transactions are not supported).
        if (_unitOfWork?.Transaction is { } transaction && Database.CurrentTransaction is null)
        {
            await Database.UseTransactionAsync(transaction, cancellationToken);
        }

        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync so the request transaction is joined.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WarehouseModel>(warehouse =>
        {
            warehouse.ToTable("warehouses", t =>
                t.HasCheckConstraint("ck_warehouses_status", $"status IN ({string.Join(", ", WarehouseStatuses.All.Select(s => $"'{s}'"))})"));
            MapCommon(warehouse);
            warehouse.Property(w => w.Code).IsRequired();
            warehouse.Property(w => w.Name).IsRequired();
            warehouse.Property(w => w.Status).IsRequired();
            warehouse.Property(w => w.UsesLocations).HasDefaultValue(false);
            warehouse.HasIndex(w => new { w.TenantId, w.Code }).IsUnique().HasDatabaseName(CodeUniqueIndex);
            // At most one default warehouse per tenant (data-model §5.6).
            warehouse.HasIndex(w => w.TenantId).IsUnique().HasFilter("is_default").HasDatabaseName(DefaultUniqueIndex);
            warehouse.HasQueryFilter(w => w.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<WarehouseLocation>(location =>
        {
            location.ToTable("warehouse_locations");
            MapCommon(location);
            location.Property(l => l.Code).IsRequired();
            location.Property(l => l.IsActive).HasDefaultValue(true);
            location.HasOne<WarehouseModel>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            location.HasIndex(l => new { l.TenantId, l.WarehouseId, l.Code }).IsUnique().HasDatabaseName(LocationCodeUniqueIndex);
            location.HasIndex(l => new { l.TenantId, l.Barcode }).IsUnique().HasFilter("barcode IS NOT NULL").HasDatabaseName(LocationBarcodeUniqueIndex);
            location.HasIndex(l => new { l.TenantId, l.WarehouseId, l.PickSequence });
            location.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });
    }

    private static void MapCommon<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : Domain.WarehouseModuleEntity
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedNever();
        // UQ (tenant_id, id): target of the tenant-safe composite foreign keys (data-model §1).
        entity.HasAlternateKey(e => new { e.TenantId, e.Id });
        entity.Property(e => e.RowVersion).IsConcurrencyToken();
    }

    private static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);

    private static void Finish(DbContextOptionsBuilder builder) =>
        builder.UseSnakeCaseNamingConvention().AddInterceptors(TenantGuard);
}

/// <summary>
/// For <c>dotnet ef</c>. Applying migrations uses the schema-owner role: set <c>ConnectionStrings__Migrator</c>
/// (never the runtime <c>bayios_app</c> connection). Adding migrations needs no database.
/// </summary>
internal sealed class WarehouseDesignTimeDbContextFactory : IDesignTimeDbContextFactory<WarehouseDbContext>
{
    public WarehouseDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<WarehouseDbContext>();
        WarehouseDbContext.Configure(builder, connectionString);
        return new WarehouseDbContext(builder.Options, new RequestContext());
    }
}
