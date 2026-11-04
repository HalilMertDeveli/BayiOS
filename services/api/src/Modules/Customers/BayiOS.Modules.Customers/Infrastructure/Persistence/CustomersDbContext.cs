using System.Data.Common;
using BayiOS.Modules.Customers.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Customers.Infrastructure.Persistence;

internal sealed class CustomersDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "customers";

    // Index names the application maps to stable problem codes.
    public const string CodeUniqueIndex = "ux_customers_tenant_id_code";

    /// <summary>Case-insensitive name per tenant among non-archived groups (expression index, created in SQL).</summary>
    public const string GroupNameUniqueIndex = "ux_dealer_groups_tenant_id_lower_name";

    public const string DefaultAddressUniqueIndex = "ux_customer_addresses_tenant_id_customer_id_type_default";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public CustomersDbContext(DbContextOptions<CustomersDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<DealerGroup> DealerGroups => Set<DealerGroup>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerAddress> Addresses => Set<CustomerAddress>();

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

        modelBuilder.Entity<DealerGroup>(group =>
        {
            group.ToTable("dealer_groups", t =>
            {
                t.HasCheckConstraint("ck_dealer_groups_status", $"status IN ({Quoted(DealerGroupStatuses.All)})");
                t.HasCheckConstraint(
                    "ck_dealer_groups_discount", "default_discount_percent IS NULL OR (default_discount_percent >= 0 AND default_discount_percent <= 100)");
            });
            MapCommon(group);
            group.Property(g => g.Name).IsRequired();
            group.Property(g => g.Status).IsRequired();
            group.Property(g => g.DefaultDiscountPercent).HasPrecision(5, 2);
            group.HasQueryFilter(g => g.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers", t =>
            {
                t.HasCheckConstraint("ck_customers_status", $"status IN ({Quoted(CustomerStatuses.All)})");
                t.HasCheckConstraint("ck_customers_currency", "currency IS NULL OR currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_customers_credit_limit", "credit_limit IS NULL OR credit_limit >= 0");
                t.HasCheckConstraint("ck_customers_payment_term_days", "payment_term_days IS NULL OR payment_term_days BETWEEN 0 AND 3650");
                t.HasCheckConstraint("ck_customers_tax_number", "tax_number IS NULL OR tax_number ~ '^[0-9]{10,11}$'");
            });
            MapCommon(customer);
            customer.Property(c => c.Code).IsRequired();
            customer.Property(c => c.Name).IsRequired();
            customer.Property(c => c.Status).IsRequired();
            customer.Property(c => c.Currency).HasColumnType("char(3)");
            customer.Property(c => c.CreditLimit).HasPrecision(18, 4);
            customer.Property(c => c.WhatsappNumbers).IsRequired().HasDefaultValueSql("'{}'::text[]");
            customer.HasOne<DealerGroup>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.DealerGroupId })
                .HasPrincipalKey(g => new { g.TenantId, g.Id })
                .OnDelete(DeleteBehavior.Restrict);
            customer.HasIndex(c => new { c.TenantId, c.Code }).IsUnique().HasDatabaseName(CodeUniqueIndex);
            customer.HasIndex(c => new { c.TenantId, c.TaxNumber });
            customer.HasIndex(c => new { c.TenantId, c.Name, c.Id });
            customer.HasIndex(c => c.WhatsappNumbers).HasMethod("gin");
            customer.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<CustomerAddress>(address =>
        {
            address.ToTable("customer_addresses", t =>
            {
                t.HasCheckConstraint("ck_customer_addresses_type", $"type IN ({Quoted(AddressTypes.All)})");
                t.HasCheckConstraint("ck_customer_addresses_country", "country ~ '^[A-Z]{2}$'");
            });
            MapCommon(address);
            address.Property(a => a.Type).IsRequired();
            address.Property(a => a.Line1).IsRequired();
            address.Property(a => a.City).IsRequired();
            address.Property(a => a.Country).IsRequired().HasColumnType("char(2)");
            address.HasOne<Customer>().WithMany()
                .HasForeignKey(a => new { a.TenantId, a.CustomerId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);
            address.HasIndex(a => new { a.TenantId, a.CustomerId, a.Type }).IsUnique().HasFilter("is_default").HasDatabaseName(DefaultAddressUniqueIndex);
            address.HasIndex(a => new { a.TenantId, a.CustomerId, a.CreatedAt });
            address.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });
    }

    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private static void MapCommon<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : CustomersEntity
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
internal sealed class CustomersDesignTimeDbContextFactory : IDesignTimeDbContextFactory<CustomersDbContext>
{
    public CustomersDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<CustomersDbContext>();
        CustomersDbContext.Configure(builder, connectionString);
        return new CustomersDbContext(builder.Options, new RequestContext());
    }
}
