using System.Data.Common;
using BayiOS.Modules.Pricing.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Pricing.Infrastructure.Persistence;

internal sealed class PricingDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "pricing";

    // Index names the application maps to stable problem codes.
    public const string NameUniqueIndex = "ux_price_lists_tenant_id_name";
    public const string DefaultUniqueIndex = "ux_price_lists_tenant_id_currency_default";
    public const string ItemUniqueIndex = "ux_price_list_items_tenant_id_list_id_variant_id_min_qty";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public PricingDbContext(DbContextOptions<PricingDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<PriceList> PriceLists => Set<PriceList>();

    public DbSet<PriceListItem> Items => Set<PriceListItem>();

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

        modelBuilder.Entity<PriceList>(list =>
        {
            list.ToTable("price_lists", t =>
            {
                t.HasCheckConstraint("ck_price_lists_kind", $"kind IN ({Quoted(PriceListKinds.All)})");
                t.HasCheckConstraint("ck_price_lists_status", $"status IN ({Quoted(PriceListStatuses.All)})");
                t.HasCheckConstraint("ck_price_lists_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_price_lists_validity", "valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from");
                t.HasCheckConstraint("ck_price_lists_default_sales", "NOT is_default OR (kind = 'sales' AND status = 'active')");
                t.HasCheckConstraint("ck_price_lists_discount", "discount_percent IS NULL OR (discount_percent >= 0 AND discount_percent <= 100)");
                t.HasCheckConstraint("ck_price_lists_base_rule", "(base_price_list_id IS NULL) = (discount_percent IS NULL) AND base_price_list_id IS DISTINCT FROM id");
                t.HasCheckConstraint("ck_price_lists_base_sales", "base_price_list_id IS NULL OR kind = 'sales'");
            });
            MapCommon(list);
            list.Property(l => l.Name).IsRequired();
            list.Property(l => l.Kind).IsRequired();
            list.Property(l => l.Currency).IsRequired().HasColumnType("char(3)");
            list.Property(l => l.Status).IsRequired();
            list.Property(l => l.PricesIncludeTax).HasDefaultValue(false);
            list.Property(l => l.DiscountPercent).HasPrecision(5, 2);
            // Base list in the same tenant (tenant-safe composite FK, data-model §1).
            list.HasOne<PriceList>().WithMany()
                .HasForeignKey(l => new { l.TenantId, l.BasePriceListId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            list.HasIndex(l => new { l.TenantId, l.Name }).IsUnique().HasFilter("status <> 'archived'").HasDatabaseName(NameUniqueIndex);
            // At most one default sales list per tenant and currency (data-model §5.4).
            list.HasIndex(l => new { l.TenantId, l.Currency }).IsUnique().HasFilter("is_default AND kind = 'sales'").HasDatabaseName(DefaultUniqueIndex);
            list.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PriceListItem>(item =>
        {
            item.ToTable("price_list_items", t =>
            {
                t.HasCheckConstraint("ck_price_list_items_min_quantity", "min_quantity > 0");
                t.HasCheckConstraint("ck_price_list_items_unit_price", "unit_price >= 0");
            });
            MapCommon(item);
            item.Property(i => i.MinQuantity).HasPrecision(18, 3).HasDefaultValue(1m);
            item.Property(i => i.UnitPrice).HasPrecision(18, 4);
            item.HasOne<PriceList>().WithMany()
                .HasForeignKey(i => new { i.TenantId, i.PriceListId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Restrict);
            item.HasIndex(i => new { i.TenantId, i.PriceListId, i.VariantId, i.MinQuantity }).IsUnique().HasDatabaseName(ItemUniqueIndex);
            item.HasIndex(i => new { i.TenantId, i.VariantId });
            item.HasQueryFilter(i => i.TenantId == CurrentTenantId);
        });
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private static void MapCommon<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : PricingEntity
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
internal sealed class PricingDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PricingDbContext>
{
    public PricingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<PricingDbContext>();
        PricingDbContext.Configure(builder, connectionString);
        return new PricingDbContext(builder.Options, new RequestContext());
    }
}
