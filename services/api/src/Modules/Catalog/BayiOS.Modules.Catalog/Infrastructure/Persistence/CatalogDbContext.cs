using System.Data.Common;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BayiOS.Modules.Catalog.Infrastructure.Persistence;

internal sealed class CatalogDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "catalog";

    // Constraint names the application maps to stable problem codes.
    public const string SkuUniqueIndex = "ux_product_variants_tenant_id_sku";
    public const string BarcodeUniqueIndex = "ux_product_variants_tenant_id_barcode";
    public const string UnitCodeUniqueIndex = "ux_units_tenant_id_code";
    public const string BrandNameUniqueIndex = "ux_brands_tenant_id_lower_name";
    public const string CategoryNameUniqueIndex = "ux_categories_tenant_id_parent_id_lower_name";
    public const string CustomFieldKeyUniqueIndex = "ux_custom_field_definitions_tenant_id_key";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> Variants => Set<ProductVariant>();

    public DbSet<CustomFieldDefinition> CustomFields => Set<CustomFieldDefinition>();

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
        // Join the request transaction so EF does not try to open its own (nested transactions are not supported).
        await JoinTransactionAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync so the request transaction is joined.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Unit>(unit =>
        {
            unit.ToTable("units");
            MapCommon(unit);
            unit.Property(u => u.Code).IsRequired();
            unit.Property(u => u.Name).IsRequired();
            unit.HasIndex(u => new { u.TenantId, u.Code }).IsUnique().HasDatabaseName(UnitCodeUniqueIndex);
            unit.HasQueryFilter(u => u.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Brand>(brand =>
        {
            brand.ToTable("brands");
            MapCommon(brand);
            brand.Property(b => b.Name).IsRequired();
            // UQ (tenant_id, lower(name)) is an expression index created in the migration SQL.
            brand.HasQueryFilter(b => b.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Category>(category =>
        {
            category.ToTable("categories", t =>
                t.HasCheckConstraint("ck_categories_depth", $"depth BETWEEN 1 AND {Category.MaxDepth}"));
            MapCommon(category);
            category.Property(c => c.Name).IsRequired();
            category.Property(c => c.Path).IsRequired();
            category.HasOne<Category>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.ParentId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // UQ NULLS NOT DISTINCT (tenant_id, parent_id, lower(name)) and the path prefix index are in the migration SQL.
            category.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Product>(product =>
        {
            product.ToTable("products", t =>
            {
                t.HasCheckConstraint("ck_products_status", StatusCheck());
                t.HasCheckConstraint("ck_products_tax_rate", "tax_rate BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_products_attributes_object", "jsonb_typeof(attributes) = 'object'");
            });
            MapCommon(product);
            product.Property(p => p.Name).IsRequired();
            product.Property(p => p.TaxRate).HasPrecision(5, 2);
            product.Property(p => p.Status).IsRequired();
            product.Property(p => p.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            product.HasOne<Category>().WithMany()
                .HasForeignKey(p => new { p.TenantId, p.CategoryId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);
            product.HasOne<Brand>().WithMany()
                .HasForeignKey(p => new { p.TenantId, p.BrandId })
                .HasPrincipalKey(b => new { b.TenantId, b.Id })
                .OnDelete(DeleteBehavior.Restrict);
            product.HasOne<Unit>().WithMany()
                .HasForeignKey(p => new { p.TenantId, p.DefaultUnitId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
            product.HasIndex(p => new { p.TenantId, p.Status, p.Name });
            product.HasIndex(p => new { p.TenantId, p.CreatedAt, p.Id });
            product.HasIndex(p => new { p.TenantId, p.Name, p.Id });
            // GIN (name gin_trgm_ops) and GIN (attributes jsonb_path_ops) are in the migration SQL.
            product.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ProductVariant>(variant =>
        {
            variant.ToTable("product_variants", t =>
            {
                t.HasCheckConstraint("ck_product_variants_status", StatusCheck());
                t.HasCheckConstraint("ck_product_variants_attributes_object", "jsonb_typeof(attributes) = 'object'");
                t.HasCheckConstraint("ck_product_variants_option_values_object", "jsonb_typeof(option_values) = 'object'");
            });
            MapCommon(variant);
            variant.Property(v => v.Sku).IsRequired();
            variant.Property(v => v.Status).IsRequired();
            variant.Property(v => v.OptionValues).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            variant.Property(v => v.Attributes).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            variant.HasOne<Product>().WithMany()
                .HasForeignKey(v => new { v.TenantId, v.ProductId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
            variant.HasOne<Unit>().WithMany()
                .HasForeignKey(v => new { v.TenantId, v.UnitId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
            variant.HasIndex(v => new { v.TenantId, v.Sku }).IsUnique().HasDatabaseName(SkuUniqueIndex);
            variant.HasIndex(v => new { v.TenantId, v.Barcode }).IsUnique().HasDatabaseName(BarcodeUniqueIndex);
            variant.HasIndex(v => new { v.TenantId, v.ProductId });
            variant.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<CustomFieldDefinition>(field =>
        {
            field.ToTable("custom_field_definitions", t =>
            {
                t.HasCheckConstraint("ck_custom_field_definitions_entity_type", InCheck("entity_type", CustomFieldEntityTypes.All));
                t.HasCheckConstraint("ck_custom_field_definitions_data_type", InCheck("data_type", CustomFieldDataTypes.All));
                t.HasCheckConstraint("ck_custom_field_definitions_key", "key ~ '^[a-z][a-z0-9_]{0,49}$'");
                t.HasCheckConstraint("ck_custom_field_definitions_options_array", "jsonb_typeof(options) = 'array'");
            });
            MapCommon(field);
            field.Property(f => f.EntityType).IsRequired();
            field.Property(f => f.Key).IsRequired();
            field.Property(f => f.Label).IsRequired();
            field.Property(f => f.DataType).IsRequired();
            field.Property(f => f.Options).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            // One key per tenant across products and variants (attr.<key> filters stay unambiguous); archived keys
            // are not reused. data-model §5.3.
            field.HasIndex(f => new { f.TenantId, f.Key }).IsUnique().HasDatabaseName(CustomFieldKeyUniqueIndex);
            field.HasIndex(f => new { f.TenantId, f.EntityType, f.SortOrder });
            field.HasQueryFilter(f => f.TenantId == CurrentTenantId);
        });
    }

    private static string InCheck(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    private static void MapCommon<T>(EntityTypeBuilder<T> entity)
        where T : CatalogEntity
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedNever();
        // UQ (tenant_id, id): target of the tenant-safe composite foreign keys (data-model §1).
        entity.HasAlternateKey(e => new { e.TenantId, e.Id });
        entity.Property(e => e.RowVersion).IsConcurrencyToken();
    }

    private static string StatusCheck() =>
        $"status IN ({string.Join(", ", CatalogStatuses.All.Select(s => $"'{s}'"))})";

    private static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);

    private static void Finish(DbContextOptionsBuilder builder) =>
        builder.UseSnakeCaseNamingConvention().AddInterceptors(TenantGuard);
}

/// <summary>
/// For <c>dotnet ef</c>. Applying migrations uses the schema-owner role: set <c>ConnectionStrings__Migrator</c>
/// (never the runtime <c>bayios_app</c> connection). Adding migrations needs no database.
/// </summary>
internal sealed class CatalogDesignTimeDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogDbContext.Configure(builder, connectionString);
        return new CatalogDbContext(builder.Options, new RequestContext());
    }
}
