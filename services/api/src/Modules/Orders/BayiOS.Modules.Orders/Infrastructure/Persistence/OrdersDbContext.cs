using System.Data.Common;
using System.Text.Json;
using BayiOS.Modules.Orders.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BayiOS.Modules.Orders.Infrastructure.Persistence;

internal sealed class OrdersDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "orders";

    public const string NumberUniqueIndex = "ux_orders_tenant_id_number";

    /// <summary>Deferrable UNIQUE (tenant_id, order_id, line_no) created in SQL, so draft edits may renumber lines.</summary>
    public const string LineNoUniqueConstraint = "ux_order_lines_tenant_id_order_id_line_no";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public OrdersDbContext(DbContextOptions<OrdersDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> Lines => Set<OrderLine>();

    public DbSet<OrderStatusChange> History => Set<OrderStatusChange>();

    public DbSet<OrderShipment> Shipments => Set<OrderShipment>();

    public DbSet<OrderShipmentLine> ShipmentLines => Set<OrderShipmentLine>();

    public DbSet<OrderNumberSequence> NumberSequences => Set<OrderNumberSequence>();

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

    /// <summary>Joins the request transaction (raw SQL and SaveChanges must run inside it).</summary>
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

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders", t =>
            {
                t.HasCheckConstraint("ck_orders_status", $"status IN ({Quoted(OrderStatuses.All)})");
                t.HasCheckConstraint("ck_orders_fulfillment_status", $"fulfillment_status IN ({Quoted(FulfillmentStatuses.All)})");
                t.HasCheckConstraint("ck_orders_source", $"source IN ({Quoted(OrderSources.All)})");
                t.HasCheckConstraint("ck_orders_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_orders_totals", "subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND grand_total >= 0");
                t.HasCheckConstraint("ck_orders_cancel_reason", "status <> 'cancelled' OR cancel_reason IS NOT NULL");
            });
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).ValueGeneratedNever();
            order.HasAlternateKey(o => new { o.TenantId, o.Id });
            order.Property(o => o.RowVersion).IsConcurrencyToken();
            order.Property(o => o.Number).IsRequired();
            order.Property(o => o.Status).IsRequired();
            order.Property(o => o.FulfillmentStatus).IsRequired();
            order.Property(o => o.Source).IsRequired();
            order.Property(o => o.Currency).IsRequired().HasColumnType("char(3)");
            order.Property(o => o.Subtotal).HasPrecision(18, 4);
            order.Property(o => o.DiscountTotal).HasPrecision(18, 4);
            order.Property(o => o.TaxTotal).HasPrecision(18, 4);
            order.Property(o => o.GrandTotal).HasPrecision(18, 4);
            order.Property(o => o.ShippingAddress).HasColumnType("jsonb").HasConversion(AddressJson.Converter);
            order.Property(o => o.BillingAddress).HasColumnType("jsonb").HasConversion(AddressJson.Converter);
            order.HasMany(o => o.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.OrderId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            order.HasIndex(o => new { o.TenantId, o.Number }).IsUnique().HasDatabaseName(NumberUniqueIndex);
            order.HasIndex(o => new { o.TenantId, o.CreatedAt, o.Id }).IsDescending(false, true, true);
            order.HasIndex(o => new { o.TenantId, o.Status, o.CreatedAt, o.Id }).IsDescending(false, false, true, true);
            order.HasIndex(o => new { o.TenantId, o.CustomerId, o.CreatedAt }).IsDescending(false, false, true);
            order.HasQueryFilter(o => o.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OrderLine>(line =>
        {
            line.ToTable("order_lines", t =>
            {
                t.HasCheckConstraint("ck_order_lines_line_no", "line_no > 0");
                t.HasCheckConstraint("ck_order_lines_quantity", "quantity > 0");
                t.HasCheckConstraint("ck_order_lines_unit_price", "unit_price IS NULL OR unit_price >= 0");
                t.HasCheckConstraint("ck_order_lines_tax_rate", "tax_rate >= 0 AND tax_rate <= 100");
                t.HasCheckConstraint(
                    "ck_order_lines_discount",
                    "num_nonnulls(discount_percent, discount_amount) <= 1 "
                    + "AND (discount_percent IS NULL OR (discount_percent >= 0 AND discount_percent <= 100)) "
                    + "AND (discount_amount IS NULL OR discount_amount >= 0)");
                t.HasCheckConstraint(
                    "ck_order_lines_amounts",
                    "(unit_price IS NULL AND line_subtotal IS NULL AND line_discount IS NULL AND line_total IS NULL) "
                    + "OR (unit_price IS NOT NULL AND line_subtotal >= 0 AND line_discount >= 0 AND line_discount <= line_subtotal "
                    + "AND line_total = line_subtotal - line_discount)");
                t.HasCheckConstraint(
                    "ck_order_lines_fulfilment",
                    "qty_reserved >= 0 AND qty_shipped >= 0 AND qty_cancelled >= 0 AND qty_reserved + qty_shipped + qty_cancelled <= quantity");
            });
            line.HasKey(l => l.Id);
            line.Property(l => l.Id).ValueGeneratedNever();
            line.HasAlternateKey(l => new { l.TenantId, l.Id });
            line.Property(l => l.Sku).IsRequired();
            line.Property(l => l.Name).IsRequired();
            line.Property(l => l.Unit).IsRequired();
            line.Property(l => l.Quantity).HasPrecision(18, 3);
            line.Property(l => l.UnitPrice).HasPrecision(18, 4);
            line.Property(l => l.DiscountPercent).HasPrecision(5, 2);
            line.Property(l => l.DiscountAmount).HasPrecision(18, 4);
            line.Property(l => l.TaxRate).HasPrecision(5, 2);
            line.Property(l => l.LineSubtotal).HasPrecision(18, 4);
            line.Property(l => l.LineDiscount).HasPrecision(18, 4);
            line.Property(l => l.LineTotal).HasPrecision(18, 4);
            line.Property(l => l.QtyReserved).HasPrecision(18, 3);
            line.Property(l => l.QtyShipped).HasPrecision(18, 3);
            line.Property(l => l.QtyCancelled).HasPrecision(18, 3);
            line.Ignore(l => l.Remaining);
            line.HasIndex(l => new { l.TenantId, l.VariantId });
            line.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OrderStatusChange>(change =>
        {
            change.ToTable("order_status_history", t =>
            {
                t.HasCheckConstraint("ck_order_status_history_action", $"action IN ({Quoted(OrderActions.All)})");
                t.HasCheckConstraint("ck_order_status_history_to_status", $"to_status IN ({Quoted(OrderStatuses.All)})");
            });
            change.HasKey(c => c.Id);
            change.Property(c => c.Id).ValueGeneratedNever();
            change.Property(c => c.Action).IsRequired();
            change.Property(c => c.ToStatus).IsRequired();
            change.Property(c => c.FulfillmentStatus).IsRequired();
            change.HasOne<Order>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.OrderId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            change.HasIndex(c => new { c.TenantId, c.OrderId, c.ChangedAt });
            change.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OrderShipment>(shipment =>
        {
            shipment.ToTable("order_shipments", t =>
                t.HasCheckConstraint("ck_order_shipments_package_count", "package_count IS NULL OR package_count > 0"));
            shipment.HasKey(s => s.Id);
            shipment.Property(s => s.Id).ValueGeneratedNever();
            shipment.HasAlternateKey(s => new { s.TenantId, s.Id });
            shipment.HasOne<Order>().WithMany()
                .HasForeignKey(s => new { s.TenantId, s.OrderId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            shipment.HasMany(s => s.Lines).WithOne()
                .HasForeignKey(l => new { l.TenantId, l.ShipmentId })
                .HasPrincipalKey(s => new { s.TenantId, s.Id })
                .OnDelete(DeleteBehavior.Restrict);
            shipment.HasIndex(s => new { s.TenantId, s.OrderId, s.ShippedAt });
            shipment.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OrderShipmentLine>(line =>
        {
            line.ToTable("order_shipment_lines", t => t.HasCheckConstraint("ck_order_shipment_lines_quantity", "quantity > 0"));
            line.HasKey(l => l.Id);
            line.Property(l => l.Id).ValueGeneratedNever();
            line.Property(l => l.Quantity).HasPrecision(18, 3);
            line.HasOne<OrderLine>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.OrderLineId })
                .HasPrincipalKey(o => new { o.TenantId, o.Id })
                .OnDelete(DeleteBehavior.Restrict);
            line.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OrderNumberSequence>(sequence =>
        {
            sequence.ToTable("order_number_sequences", t =>
            {
                t.HasCheckConstraint("ck_order_number_sequences_next_value", "next_value > 0");
                // Same rule as identity.tenant_settings.order_number_prefix.
                t.HasCheckConstraint("ck_order_number_sequences_prefix", "prefix ~ '^[A-Z][A-Z0-9]{0,9}$'");
            });
            sequence.HasKey(s => new { s.TenantId, s.Prefix, s.Year });
            sequence.Property(s => s.Prefix).HasDefaultValue("SIP").ValueGeneratedNever();
            sequence.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    /// <summary>Address snapshots as camelCase jsonb (records compare by value, so change tracking works as is).</summary>
    private static class AddressJson
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        // EF never passes null through a converter (null stays SQL NULL); the nullable signature only matches the property.
        public static readonly ValueConverter<OrderAddressSnapshot?, string> Converter = new(
            v => JsonSerializer.Serialize(v, Options),
            s => JsonSerializer.Deserialize<OrderAddressSnapshot>(s, Options));
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
internal sealed class OrdersDesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<OrdersDbContext>();
        OrdersDbContext.Configure(builder, connectionString);
        return new OrdersDbContext(builder.Options, new RequestContext());
    }
}
