using System.Data.Common;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Inventory.Infrastructure.Persistence;

internal sealed class InventoryDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "inventory";

    // Constraint/index names the engine maps to stable problem codes.
    public const string BalanceKeyIndex = "ux_inventory_balances_key";
    public const string MovementSourceLineIndex = "ux_inventory_movements_source_line";
    public const string ActiveReservationIndex = "ux_stock_reservations_active_line";
    public const string ThresholdKeyIndex = "ux_stock_thresholds_key";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;

    public InventoryDbContext(DbContextOptions<InventoryDbContext> options, IRequestContext requestContext)
        : base(options)
    {
        _requestContext = requestContext;
    }

    public DbSet<InventoryMovement> Movements => Set<InventoryMovement>();

    public DbSet<InventoryBalance> Balances => Set<InventoryBalance>();

    public DbSet<StockReservation> Reservations => Set<StockReservation>();

    public DbSet<StockThreshold> Thresholds => Set<StockThreshold>();

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

    // The engine writes with SQL on the UnitOfWork transaction; tracked writes through this context are not allowed.
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Inventory rows are written only by the stock engine.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Inventory rows are written only by the stock engine.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<InventoryMovement>(movement =>
        {
            movement.ToTable("inventory_movements", t =>
            {
                t.HasCheckConstraint("ck_inventory_movements_type", InList("type", MovementTypes.All));
                t.HasCheckConstraint("ck_inventory_movements_source_type", InList("source_type", MovementSourceTypes.All));
                t.HasCheckConstraint("ck_inventory_movements_quantity_nonzero", "quantity <> 0");
                t.HasCheckConstraint(
                    "ck_inventory_movements_quantity_sign",
                    $"({InList("type", MovementTypes.Inbound)} AND quantity > 0) OR ({InList("type", MovementTypes.Outbound)} AND quantity < 0) OR type = '{MovementTypes.CountCorrection}'");
                t.HasCheckConstraint("ck_inventory_movements_unit_cost", "unit_cost IS NULL OR unit_cost >= 0");
            });
            movement.HasKey(m => m.Id);
            movement.Property(m => m.Id).ValueGeneratedNever();
            movement.Property(m => m.Type).IsRequired();
            movement.Property(m => m.Quantity).HasPrecision(18, 3);
            movement.Property(m => m.UnitCost).HasPrecision(18, 4);
            movement.Property(m => m.SourceType).IsRequired();
            movement.Property(m => m.BalanceAfter).HasPrecision(18, 3);
            movement.Property(m => m.OccurredAt).HasDefaultValueSql("now()");
            // Stock card: per variant (+ warehouse), newest first; keyset by (occurred_at, id).
            movement.HasIndex(m => new { m.TenantId, m.VariantId, m.WarehouseId, m.OccurredAt })
                .IsDescending(false, false, false, true);
            movement.HasIndex(m => new { m.TenantId, m.VariantId, m.OccurredAt, m.Id })
                .IsDescending(false, false, true, true);
            movement.HasIndex(m => new { m.TenantId, m.SourceType, m.SourceId });
            // The same source line is never applied twice (data-model §5.7; idempotency backstop).
            movement.HasIndex(m => new { m.TenantId, m.SourceType, m.SourceLineId, m.Type, m.LocationId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("source_line_id IS NOT NULL")
                .HasDatabaseName(MovementSourceLineIndex);
            movement.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InventoryBalance>(balance =>
        {
            balance.ToTable("inventory_balances", t =>
            {
                t.HasCheckConstraint("ck_inventory_balances_reserved_nonnegative", "reserved >= 0");
                t.HasCheckConstraint("ck_inventory_balances_no_negative", "allow_negative OR (on_hand >= 0 AND reserved <= on_hand)");
            });
            balance.HasKey(b => b.Id);
            balance.Property(b => b.Id).ValueGeneratedNever();
            balance.Property(b => b.OnHand).HasPrecision(18, 3).HasDefaultValue(0m);
            balance.Property(b => b.Reserved).HasPrecision(18, 3).HasDefaultValue(0m);
            balance.Property(b => b.AllowNegative).HasDefaultValue(false);
            balance.Property(b => b.Version).HasDefaultValue(0L);
            balance.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
            balance.HasIndex(b => new { b.TenantId, b.VariantId, b.WarehouseId, b.LocationId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName(BalanceKeyIndex);
            balance.HasIndex(b => new { b.TenantId, b.WarehouseId, b.VariantId });
            balance.HasQueryFilter(b => b.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StockReservation>(reservation =>
        {
            reservation.ToTable("stock_reservations", t =>
            {
                t.HasCheckConstraint("ck_stock_reservations_status", InList("status", ReservationStatuses.All));
                t.HasCheckConstraint("ck_stock_reservations_quantity", "quantity > 0");
                t.HasCheckConstraint(
                    "ck_stock_reservations_progress",
                    "consumed_quantity >= 0 AND released_quantity >= 0 AND consumed_quantity + released_quantity <= quantity");
                t.HasCheckConstraint(
                    "ck_stock_reservations_closed",
                    "status = 'active' OR consumed_quantity + released_quantity = quantity");
            });
            reservation.HasKey(r => r.Id);
            reservation.Property(r => r.Id).ValueGeneratedNever();
            reservation.Property(r => r.Quantity).HasPrecision(18, 3);
            reservation.Property(r => r.ConsumedQuantity).HasPrecision(18, 3).HasDefaultValue(0m);
            reservation.Property(r => r.ReleasedQuantity).HasPrecision(18, 3).HasDefaultValue(0m);
            reservation.Property(r => r.Status).IsRequired();
            reservation.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            reservation.HasIndex(r => new { r.TenantId, r.OrderId }).HasFilter("status = 'active'");
            reservation.HasIndex(r => new { r.TenantId, r.VariantId, r.WarehouseId }).HasFilter("status = 'active'");
            reservation.HasIndex(r => new { r.TenantId, r.OrderLineId, r.LocationId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("status = 'active'")
                .HasDatabaseName(ActiveReservationIndex);
            reservation.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StockThreshold>(threshold =>
        {
            threshold.ToTable("stock_thresholds", t =>
                t.HasCheckConstraint("ck_stock_thresholds_min_quantity", "min_quantity >= 0"));
            threshold.HasKey(t => t.Id);
            threshold.Property(t => t.Id).ValueGeneratedNever();
            threshold.Property(t => t.MinQuantity).HasPrecision(18, 3);
            threshold.Property(t => t.Version).HasDefaultValue(1L);
            threshold.Property(t => t.CreatedAt).HasDefaultValueSql("now()");

            // One default (warehouse NULL) and one override per warehouse for each variant (data-model §5.13).
            threshold.HasIndex(t => new { t.TenantId, t.VariantId, t.WarehouseId })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName(ThresholdKeyIndex);
            threshold.HasIndex(t => new { t.TenantId, t.WarehouseId });
            threshold.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });
    }

    private static string InList(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    private static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);

    private static void Finish(DbContextOptionsBuilder builder) =>
        builder.UseSnakeCaseNamingConvention().AddInterceptors(TenantGuard);
}

/// <summary>
/// For <c>dotnet ef</c>. Applying migrations uses the schema-owner role: set <c>ConnectionStrings__Migrator</c>
/// (never the runtime <c>bayios_app</c> connection). Adding migrations needs no database.
/// </summary>
internal sealed class InventoryDesignTimeDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<InventoryDbContext>();
        InventoryDbContext.Configure(builder, connectionString);
        return new InventoryDbContext(builder.Options, new RequestContext());
    }
}
