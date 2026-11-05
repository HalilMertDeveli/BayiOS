using System.Data.Common;
using BayiOS.Modules.Billing.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Billing.Infrastructure.Persistence;

/// <summary>
/// The <c>billing</c> schema (data-model §5.2): tenant subscription, add-on activations and operator overrides. The
/// plan/add-on catalog itself is code (<see cref="BillingCatalog"/>), not a table.
/// </summary>
internal sealed class BillingDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "billing";
    public const string SubscriptionTenantUniqueIndex = "ux_tenant_subscriptions_tenant_id";
    public const string ModuleUniqueIndex = "ux_tenant_modules_tenant_id_module_code";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public BillingDbContext(DbContextOptions<BillingDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<TenantSubscription> Subscriptions => Set<TenantSubscription>();

    public DbSet<TenantModule> Modules => Set<TenantModule>();

    public DbSet<EntitlementOverride> Overrides => Set<EntitlementOverride>();

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

        modelBuilder.Entity<TenantSubscription>(subscription =>
        {
            subscription.ToTable("tenant_subscriptions", t =>
            {
                t.HasCheckConstraint("ck_tenant_subscriptions_plan_code", $"plan_code IN ({Quoted(BillingCatalog.Plans.Select(p => p.Code))})");
                t.HasCheckConstraint("ck_tenant_subscriptions_status", $"status IN ({Quoted(SubscriptionStatuses.All)})");
                t.HasCheckConstraint(
                    "ck_tenant_subscriptions_trial",
                    "status <> 'trialing' OR (trial_started_at IS NOT NULL AND trial_ends_at IS NOT NULL AND trial_ends_at > trial_started_at)");
                t.HasCheckConstraint("ck_tenant_subscriptions_period", "current_period_end IS NULL OR current_period_end > current_period_start");
            });
            subscription.HasKey(s => s.Id);
            subscription.Property(s => s.Id).ValueGeneratedNever();
            subscription.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName(SubscriptionTenantUniqueIndex);
            subscription.Property(s => s.RowVersion).IsConcurrencyToken();
            subscription.Property(s => s.PlanCode).IsRequired();
            subscription.Property(s => s.Status).IsRequired();
            subscription.Property(s => s.CatalogVersion).IsRequired();

            // Tenant-less expiry sweep (billing.due_subscription_tenants).
            subscription.HasIndex(s => s.TrialEndsAt).HasDatabaseName("ix_tenant_subscriptions_trialing_ends_at").HasFilter("status = 'trialing'");
            subscription.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenantModule>(module =>
        {
            module.ToTable("tenant_modules", t =>
            {
                t.HasCheckConstraint("ck_tenant_modules_module_code", $"module_code IN ({Quoted(BillingCatalog.Modules.Select(m => m.Code))})");
                t.HasCheckConstraint("ck_tenant_modules_status", $"status IN ({Quoted(ModuleStatuses.All)})");
                t.HasCheckConstraint("ck_tenant_modules_expiry", "status = 'active' OR expires_at IS NOT NULL");
            });
            module.HasKey(m => m.Id);
            module.Property(m => m.Id).ValueGeneratedNever();
            module.HasIndex(m => new { m.TenantId, m.ModuleCode }).IsUnique().HasDatabaseName(ModuleUniqueIndex);
            module.Property(m => m.RowVersion).IsConcurrencyToken();
            module.Property(m => m.ModuleCode).IsRequired();
            module.Property(m => m.Status).IsRequired();
            module.HasIndex(m => m.ExpiresAt).HasDatabaseName("ix_tenant_modules_trialing_expires_at").HasFilter("status = 'trialing'");
            module.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<EntitlementOverride>(item =>
        {
            item.ToTable("tenant_entitlements", t =>
            {
                t.HasCheckConstraint("ck_tenant_entitlements_feature_code", "feature_code ~ '^[a-z][a-z0-9_]{1,62}$'");
                t.HasCheckConstraint("ck_tenant_entitlements_source", "source IN ('override')");
                t.HasCheckConstraint("ck_tenant_entitlements_limit_value", "limit_value IS NULL OR limit_value >= 0");
            });
            item.HasKey(o => o.Id);
            item.Property(o => o.Id).ValueGeneratedNever();
            item.HasIndex(o => new { o.TenantId, o.FeatureCode }).IsUnique();
            item.Property(o => o.FeatureCode).IsRequired();
            item.Property(o => o.Source).IsRequired().HasDefaultValue("override");
            item.HasQueryFilter(o => o.TenantId == CurrentTenantId);
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
internal sealed class BillingDesignTimeDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<BillingDbContext>();
        BillingDbContext.Configure(builder, connectionString);
        return new BillingDbContext(builder.Options, new RequestContext());
    }
}
