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
    public const string ActivePickTaskUniqueIndex = "ux_picking_tasks_tenant_id_order_id_active";
    public const string ShipmentNumberUniqueIndex = "ux_shipments_tenant_id_number";
    public const string OpenShipmentUniqueIndex = "ux_shipments_tenant_id_order_id_open";
    public const string TransferNumberUniqueIndex = "ux_stock_transfers_tenant_id_number";
    public const string CountNumberUniqueIndex = "ux_stock_counts_tenant_id_number";
    public const string ActiveCountUniqueIndex = "ux_stock_counts_tenant_id_warehouse_id_active";

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

    public DbSet<PickingTask> PickingTasks => Set<PickingTask>();

    public DbSet<PickingTaskLine> PickingTaskLines => Set<PickingTaskLine>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();

    public DbSet<ShipmentPackage> ShipmentPackages => Set<ShipmentPackage>();

    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();

    public DbSet<StockTransferLine> StockTransferLines => Set<StockTransferLine>();

    public DbSet<StockCount> StockCounts => Set<StockCount>();

    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();

    public Guid? CurrentTenantId => _requestContext.TenantId;

    /// <summary>Joins the request transaction (raw SQL and EF queries must run inside it).</summary>
    public async Task JoinTransactionAsync(CancellationToken cancellationToken)
    {
        if (_unitOfWork?.Transaction is { } transaction && Database.CurrentTransaction is null)
        {
            await Database.UseTransactionAsync(transaction, cancellationToken);
        }
    }

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

        MapFulfillment(modelBuilder);
        MapTransfersAndCounts(modelBuilder);
    }

    /// <summary>Two-stage warehouse transfers (OD-01) and stock counts (INV-06), #168.</summary>
    private void MapTransfersAndCounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockTransfer>(transfer =>
        {
            transfer.ToTable("stock_transfers", t =>
            {
                t.HasCheckConstraint("ck_stock_transfers_status", $"status IN ({Quoted(StockTransferStatuses.All)})");
                t.HasCheckConstraint("ck_stock_transfers_warehouses", "source_warehouse_id <> destination_warehouse_id");
                t.HasCheckConstraint(
                    "ck_stock_transfers_close_resolution",
                    $"(status = 'closed') = (close_resolution IS NOT NULL) AND (close_resolution IS NULL OR close_resolution IN ({Quoted(TransferResolutions.All)}))");
                t.HasCheckConstraint(
                    "ck_stock_transfers_dispatched",
                    "status IN ('draft', 'cancelled') OR (dispatched_at IS NOT NULL AND dispatched_by IS NOT NULL)");
            });
            MapCommon(transfer);
            transfer.Property(t => t.Number).IsRequired();
            transfer.Property(t => t.Status).IsRequired();
            transfer.HasOne<WarehouseModel>().WithMany()
                .HasForeignKey(t => new { t.TenantId, t.SourceWarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            transfer.HasOne<WarehouseModel>().WithMany()
                .HasForeignKey(t => new { t.TenantId, t.DestinationWarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            transfer.HasMany(t => t.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.TransferId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
            transfer.HasIndex(t => new { t.TenantId, t.Number }).IsUnique().HasDatabaseName(TransferNumberUniqueIndex);
            transfer.HasIndex(t => new { t.TenantId, t.CreatedAt, t.Id });
            transfer.HasIndex(t => new { t.TenantId, t.Status, t.CreatedAt });
            transfer.HasIndex(t => new { t.TenantId, t.SourceWarehouseId, t.Status });
            transfer.HasIndex(t => new { t.TenantId, t.DestinationWarehouseId, t.Status });
            transfer.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StockTransferLine>(line =>
        {
            line.ToTable("stock_transfer_lines", t => t.HasCheckConstraint(
                "ck_stock_transfer_lines_quantities",
                "quantity > 0 AND received_quantity >= 0 AND returned_quantity >= 0 AND lost_quantity >= 0 "
                + "AND received_quantity + returned_quantity + lost_quantity <= quantity"));
            line.HasKey(l => l.Id);
            line.Property(l => l.Id).ValueGeneratedNever();
            line.HasAlternateKey(l => new { l.TenantId, l.Id });
            line.Property(l => l.Sku).IsRequired();
            line.Property(l => l.Quantity).HasPrecision(18, 3);
            line.Property(l => l.ReceivedQuantity).HasPrecision(18, 3);
            line.Property(l => l.ReturnedQuantity).HasPrecision(18, 3);
            line.Property(l => l.LostQuantity).HasPrecision(18, 3);
            line.Ignore(l => l.Outstanding);
            line.HasOne<WarehouseLocation>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.SourceLocationId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            line.HasOne<WarehouseLocation>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.DestinationLocationId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            line.HasIndex(l => new { l.TenantId, l.TransferId, l.LineNo }).IsUnique();
            line.HasIndex(l => new { l.TenantId, l.TransferId, l.VariantId, l.SourceLocationId }).IsUnique().AreNullsDistinct(false);
            line.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StockCount>(count =>
        {
            count.ToTable("stock_counts", t =>
            {
                t.HasCheckConstraint("ck_stock_counts_status", $"status IN ({Quoted(StockCountStatuses.All)})");
                t.HasCheckConstraint("ck_stock_counts_scope", $"scope IN ({Quoted(StockCountScopes.All)})");
                t.HasCheckConstraint("ck_stock_counts_applied", "status <> 'applied' OR (applied_at IS NOT NULL AND applied_by IS NOT NULL)");
            });
            MapCommon(count);
            count.Property(c => c.Number).IsRequired();
            count.Property(c => c.Scope).IsRequired();
            count.Property(c => c.Status).IsRequired();
            count.Property(c => c.ScopeLocationIds).HasColumnType("uuid[]");
            count.Property(c => c.ScopeVariantIds).HasColumnType("uuid[]");
            count.HasOne<WarehouseModel>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            count.HasMany(c => c.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.StockCountId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);
            count.HasIndex(c => new { c.TenantId, c.Number }).IsUnique().HasDatabaseName(CountNumberUniqueIndex);
            count.HasIndex(c => new { c.TenantId, c.CreatedAt, c.Id });
            // One active count per warehouse ("sayım açıkken aynı kapsamda ikinci sayım başlatılamaz", AF-06): the
            // concurrency-safe backstop of the create check.
            count.HasIndex(c => new { c.TenantId, c.WarehouseId }, ActiveCountUniqueIndex).IsUnique()
                .HasFilter("status IN ('open', 'submitted')")
                .HasDatabaseName(ActiveCountUniqueIndex);
            count.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StockCountLine>(line =>
        {
            line.ToTable("stock_count_lines", t =>
            {
                t.HasCheckConstraint("ck_stock_count_lines_status", $"line_status IN ({Quoted(StockCountLineStatuses.All)})");
                t.HasCheckConstraint(
                    "ck_stock_count_lines_quantities",
                    "(counted_quantity IS NULL OR counted_quantity >= 0) "
                    + "AND (counted_quantity IS NULL) = (counted_at IS NULL) "
                    + "AND (line_status NOT IN ('counted', 'applied') OR counted_quantity IS NOT NULL)");
            });
            line.HasKey(l => l.Id);
            line.Property(l => l.Id).ValueGeneratedNever();
            line.HasAlternateKey(l => new { l.TenantId, l.Id });
            line.Property(l => l.Sku).IsRequired();
            line.Property(l => l.LineStatus).IsRequired();
            line.Property(l => l.ExpectedQuantity).HasPrecision(18, 3);
            line.Property(l => l.CountedQuantity).HasPrecision(18, 3);
            line.Property(l => l.AppliedDelta).HasPrecision(18, 3);
            line.HasOne<WarehouseLocation>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.LocationId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            line.HasIndex(l => new { l.TenantId, l.StockCountId, l.VariantId, l.LocationId }).IsUnique().AreNullsDistinct(false);
            line.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });
    }

    /// <summary>Picking tasks, shipments and packages (data-model §5.6, JOB 10).</summary>
    private void MapFulfillment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PickingTask>(task =>
        {
            task.ToTable("picking_tasks", t =>
            {
                t.HasCheckConstraint("ck_picking_tasks_type", $"type IN ({Quoted(PickingTaskTypes.All)})");
                t.HasCheckConstraint("ck_picking_tasks_status", $"status IN ({Quoted(PickingTaskStatuses.All)})");
                t.HasCheckConstraint("ck_picking_tasks_priority", $"priority IN ({Quoted(PickingTaskPriorities.All)})");
            });
            MapCommon(task);
            task.Property(t => t.Type).IsRequired();
            // Default for rows created before the column existed (#98); new rows get the entity default.
            task.Property(t => t.Priority).IsRequired().HasDefaultValue(PickingTaskPriorities.Normal).ValueGeneratedNever();
            task.Property(t => t.OrderNumber).IsRequired();
            task.Property(t => t.Status).IsRequired();
            task.HasOne<WarehouseModel>().WithMany()
                .HasForeignKey(t => new { t.TenantId, t.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            task.HasMany(t => t.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.PickingTaskId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
            task.HasIndex(t => new { t.TenantId, t.WarehouseId, t.Status });
            task.HasIndex(t => new { t.TenantId, t.OrderId });
            task.HasIndex(t => new { t.TenantId, t.AssignedTo, t.Status });
            task.HasIndex(t => new { t.TenantId, t.CreatedAt, t.Id });
            // At most one active pick task per order: the last defence against double picking.
            task.HasIndex(t => new { t.TenantId, t.OrderId }, ActivePickTaskUniqueIndex).IsUnique()
                .HasFilter("type = 'pick' AND status IN ('open', 'in_progress')")
                .HasDatabaseName(ActivePickTaskUniqueIndex);
            task.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PickingTaskLine>(line =>
        {
            line.ToTable("picking_task_lines", t =>
            {
                t.HasCheckConstraint("ck_picking_task_lines_quantities", "qty_to_pick >= 0 AND qty_picked >= 0 AND qty_short >= 0 AND qty_picked + qty_short <= qty_to_pick");
                t.HasCheckConstraint("ck_picking_task_lines_shortage_reason", $"shortage_reason IS NULL OR shortage_reason IN ({Quoted(ShortageReasons.All)})");
            });
            line.HasKey(l => l.Id);
            line.Property(l => l.Id).ValueGeneratedNever();
            line.HasAlternateKey(l => new { l.TenantId, l.Id });
            line.Property(l => l.Sku).IsRequired();
            line.Property(l => l.Name).IsRequired();
            line.Property(l => l.Unit).IsRequired();
            line.Property(l => l.QtyToPick).HasPrecision(18, 3);
            line.Property(l => l.QtyPicked).HasPrecision(18, 3);
            line.Property(l => l.QtyShort).HasPrecision(18, 3);
            line.Ignore(l => l.Remaining);
            line.HasOne<WarehouseLocation>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.LocationId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            line.HasIndex(l => new { l.TenantId, l.PickingTaskId, l.Sequence }).IsUnique();
            line.HasIndex(l => new { l.TenantId, l.OrderLineId });
            line.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Shipment>(shipment =>
        {
            shipment.ToTable("shipments", t =>
            {
                t.HasCheckConstraint("ck_shipments_status", $"status IN ({Quoted(ShipmentStatuses.All)})");
                t.HasCheckConstraint("ck_shipments_package_count", "package_count IS NULL OR package_count > 0");
                t.HasCheckConstraint("ck_shipments_confirmed", "status <> 'confirmed' OR (shipped_at IS NOT NULL AND confirmed_by IS NOT NULL)");
            });
            MapCommon(shipment);
            shipment.Property(s => s.Number).IsRequired();
            shipment.Property(s => s.OrderNumber).IsRequired();
            shipment.Property(s => s.Status).IsRequired();
            shipment.HasOne<WarehouseModel>().WithMany()
                .HasForeignKey(s => new { s.TenantId, s.WarehouseId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Restrict);
            shipment.HasOne<PickingTask>().WithMany()
                .HasForeignKey(s => new { s.TenantId, s.PickingTaskId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
            shipment.HasMany(s => s.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.ShipmentId })
                .HasPrincipalKey(s => new { s.TenantId, s.Id })
                .OnDelete(DeleteBehavior.Restrict);
            shipment.HasMany(s => s.Packages).WithOne()
                .HasForeignKey(p => new { p.TenantId, p.ShipmentId })
                .HasPrincipalKey(s => new { s.TenantId, s.Id })
                .OnDelete(DeleteBehavior.Restrict);
            shipment.HasIndex(s => new { s.TenantId, s.Number }).IsUnique().HasDatabaseName(ShipmentNumberUniqueIndex);
            shipment.HasIndex(s => new { s.TenantId, s.OrderId });
            shipment.HasIndex(s => new { s.TenantId, s.CreatedAt, s.Id });
            // At most one unconfirmed shipment per order (picking completes into exactly one draft).
            shipment.HasIndex(s => new { s.TenantId, s.OrderId }, OpenShipmentUniqueIndex).IsUnique()
                .HasFilter("status IN ('draft', 'packed')")
                .HasDatabaseName(OpenShipmentUniqueIndex);
            shipment.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ShipmentLine>(line =>
        {
            line.ToTable("shipment_lines", t =>
            {
                t.HasCheckConstraint(
                    "ck_shipment_lines_quantities",
                    "picked_quantity > 0 AND quantity >= 0 AND short_quantity >= 0 AND quantity + short_quantity = picked_quantity "
                    + "AND (packed_quantity IS NULL OR (packed_quantity >= 0 AND packed_quantity <= quantity))");
                t.HasCheckConstraint("ck_shipment_lines_shortage_reason", $"shortage_reason IS NULL OR shortage_reason IN ({Quoted(ShortageReasons.All)})");
            });
            line.HasKey(l => l.Id);
            line.Property(l => l.Id).ValueGeneratedNever();
            line.HasAlternateKey(l => new { l.TenantId, l.Id });
            line.Property(l => l.Sku).IsRequired();
            line.Property(l => l.Name).IsRequired();
            line.Property(l => l.Unit).IsRequired();
            line.Property(l => l.PickedQuantity).HasPrecision(18, 3);
            line.Property(l => l.Quantity).HasPrecision(18, 3);
            line.Property(l => l.PackedQuantity).HasPrecision(18, 3);
            line.Property(l => l.ShortQuantity).HasPrecision(18, 3);
            line.HasOne<WarehouseLocation>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.LocationId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            line.HasIndex(l => new { l.TenantId, l.OrderLineId });
            line.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ShipmentPackage>(package =>
        {
            package.ToTable("shipment_packages", t => t.HasCheckConstraint("ck_shipment_packages_package_no", "package_no > 0"));
            package.HasKey(p => p.Id);
            package.Property(p => p.Id).ValueGeneratedNever();
            package.HasAlternateKey(p => new { p.TenantId, p.Id });
            package.HasMany(p => p.Items).WithOne()
                .HasForeignKey(i => new { i.TenantId, i.PackageId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
            package.HasIndex(p => new { p.TenantId, p.ShipmentId, p.PackageNo }).IsUnique();
            package.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ShipmentPackageItem>(item =>
        {
            item.ToTable("shipment_package_items", t => t.HasCheckConstraint("ck_shipment_package_items_quantity", "quantity > 0"));
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).ValueGeneratedNever();
            item.Property(i => i.Quantity).HasPrecision(18, 3);
            item.HasOne<ShipmentLine>().WithMany()
                .HasForeignKey(i => new { i.TenantId, i.ShipmentLineId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            item.HasIndex(i => new { i.TenantId, i.PackageId });
            item.HasQueryFilter(i => i.TenantId == CurrentTenantId);
        });
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

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
