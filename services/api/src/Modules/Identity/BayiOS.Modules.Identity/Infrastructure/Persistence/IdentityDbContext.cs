using System.Data.Common;
using BayiOS.Modules.Identity.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Identity.Infrastructure.Persistence;

internal sealed class IdentityDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "identity";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    /// <summary>Partial unique index: one pending invitation per e-mail per tenant.</summary>
    public const string PendingInvitationIndex = "ux_invitations_pending_email";

    public Guid? CurrentTenantId => _requestContext.TenantId;

    public Guid? CurrentUserId => _requestContext.UserId;

    /// <summary>Shared configuration for runtime, design-time and test contexts.</summary>
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

    /// <summary>Joins the request transaction (raw SQL before the first SaveChanges; nested transactions are not supported).</summary>
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

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants", t =>
            {
                t.HasCheckConstraint("ck_tenants_status", "status IN ('active', 'suspended', 'closed')");
                t.HasCheckConstraint("ck_tenants_sector", "sector IN ('motorcycle_parts', 'led_electronics', 'general')");
                t.HasCheckConstraint("ck_tenants_default_currency", "default_currency ~ '^[A-Z]{3}$'");
            });
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Name).IsRequired();
            tenant.Property(t => t.Slug).IsRequired();
            tenant.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("ux_tenants_slug");
            tenant.Property(t => t.DefaultCurrency).HasColumnType("char(3)");
            tenant.Property(t => t.RowVersion).IsConcurrencyToken();

            // Mirrors the RLS exception: own context, or a tenant the caller is a member of.
            tenant.HasQueryFilter(t => t.Id == CurrentTenantId
                || Memberships.Any(m => m.TenantId == t.Id && m.UserId == CurrentUserId));
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            membership.ToTable("memberships", t =>
            {
                t.HasCheckConstraint("ck_memberships_role", RoleCheck());
                t.HasCheckConstraint("ck_memberships_status", "status IN ('invited', 'active', 'disabled')");
            });
            membership.HasKey(m => m.Id);
            membership.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            membership.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
            membership.HasIndex(m => new { m.UserId, m.Status });

            // Tenant resolution runs before a tenant is chosen, so the caller's own rows stay visible.
            membership.HasQueryFilter(m => m.TenantId == CurrentTenantId || m.UserId == CurrentUserId);
        });

        modelBuilder.Entity<UserProfile>(profile =>
        {
            profile.ToTable("user_profiles");
            profile.HasKey(p => p.UserId);
            profile.Property(p => p.UserId).ValueGeneratedNever();
            profile.HasQueryFilter(p => p.UserId == CurrentUserId
                || Memberships.Any(m => m.UserId == p.UserId && m.TenantId == CurrentTenantId));
        });

        modelBuilder.Entity<TenantSettings>(settings =>
        {
            settings.ToTable("tenant_settings", t =>
            {
                t.HasCheckConstraint("ck_tenant_settings_negative_stock_policy", "negative_stock_policy = 'block'"); // OD-09
                t.HasCheckConstraint("ck_tenant_settings_sales_max_discount", "sales_max_discount_percent BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_tenant_settings_default_tax_rate", "default_tax_rate BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_tenant_settings_order_number_prefix", $"order_number_prefix ~ '{Domain.TenantSettings.OrderNumberPrefixPattern}'");
            });
            settings.HasKey(s => s.TenantId);
            settings.Property(s => s.TenantId).ValueGeneratedNever();
            settings.HasOne<Tenant>().WithOne().HasForeignKey<TenantSettings>(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            settings.Property(s => s.SalesMaxDiscountPercent).HasPrecision(5, 2);
            // Default for rows created before this column existed; new rows get the entity default (20).
            settings.Property(s => s.DefaultTaxRate).HasPrecision(5, 2).HasDefaultValue(20m).ValueGeneratedNever();
            // Defaults for rows created before these columns existed (#92); new rows get the entity defaults.
            settings.Property(s => s.RequireOrderApproval).HasDefaultValue(true).ValueGeneratedNever();
            settings.Property(s => s.OrderNumberPrefix).IsRequired()
                .HasDefaultValue(Domain.TenantSettings.DefaultOrderNumberPrefix).ValueGeneratedNever();
            settings.Property(s => s.RowVersion).IsConcurrencyToken();
            settings.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Invitation>(invitation =>
        {
            invitation.ToTable("invitations", t =>
            {
                t.HasCheckConstraint("ck_invitations_role", GrantableRoleCheck());
                t.HasCheckConstraint("ck_invitations_status", $"status IN ({string.Join(", ", InvitationStatuses.All.Select(s => $"'{s}'"))})");
                t.HasCheckConstraint("ck_invitations_email", "email = lower(btrim(email)) AND length(email) BETWEEN 3 AND 254 AND position('@' in email) > 1");
                t.HasCheckConstraint("ck_invitations_token_hash", "octet_length(token_hash) = 32");
                t.HasCheckConstraint("ck_invitations_token_count", "token_count >= 1");
                t.HasCheckConstraint("ck_invitations_accepted", "(status = 'accepted') = (accepted_at IS NOT NULL AND accepted_by IS NOT NULL)");
                t.HasCheckConstraint("ck_invitations_revoked", "(status = 'revoked') = (revoked_at IS NOT NULL AND revoked_by IS NOT NULL)");
                t.HasCheckConstraint("ck_invitations_expiry", "expires_at > created_at");
            });
            invitation.HasKey(i => i.Id);
            invitation.Property(i => i.Id).ValueGeneratedNever();
            invitation.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            invitation.Property(i => i.Email).IsRequired();
            invitation.Property(i => i.Role).IsRequired();
            invitation.Property(i => i.Status).IsRequired();
            invitation.Property(i => i.TokenHash).IsRequired();
            invitation.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("ux_invitations_token_hash");
            invitation.HasIndex(i => new { i.TenantId, i.Email }).IsUnique()
                .HasFilter("status = 'pending'").HasDatabaseName(PendingInvitationIndex);
            invitation.HasIndex(i => new { i.TenantId, i.Status, i.ExpiresAt }).HasDatabaseName("ix_invitations_tenant_status_expires");
            invitation.Property(i => i.RowVersion).IsConcurrencyToken();
            invitation.HasQueryFilter(i => i.TenantId == CurrentTenantId);
        });
    }

    private static string GrantableRoleCheck() =>
        $"role IN ({string.Join(", ", MemberRoleRules.Grantable.Select(r => $"'{r}'"))})";

    private static string RoleCheck() =>
        $"role IN ({string.Join(", ", Roles.All.Select(r => $"'{r}'"))})";

    private static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);

    private static void Finish(DbContextOptionsBuilder builder) =>
        builder.UseSnakeCaseNamingConvention().AddInterceptors(TenantGuard);
}

/// <summary>
/// For <c>dotnet ef</c>. Applying migrations uses the schema-owner role: set <c>ConnectionStrings__Migrator</c>
/// (never the runtime <c>bayios_app</c> connection). Adding migrations needs no database.
/// </summary>
internal sealed class IdentityDesignTimeDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(builder, connectionString);
        return new IdentityDbContext(builder.Options, new RequestContext());
    }
}
