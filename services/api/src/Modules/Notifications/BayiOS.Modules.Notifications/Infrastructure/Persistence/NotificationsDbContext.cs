using System.Data.Common;
using BayiOS.Modules.Notifications.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Notifications.Infrastructure.Persistence;

/// <summary>The <c>notifications</c> schema (data-model §5.11): push device registrations.</summary>
internal sealed class NotificationsDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "notifications";
    public const string TokenUniqueIndex = "ux_device_tokens_token_sha256";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<DeviceToken> Devices => Set<DeviceToken>();

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

        modelBuilder.Entity<DeviceToken>(device =>
        {
            device.ToTable("device_tokens", t =>
            {
                t.HasCheckConstraint("ck_device_tokens_platform", $"platform IN ({Quoted(DevicePlatforms.All)})");
                t.HasCheckConstraint(
                    "ck_device_tokens_token", $"length(token) BETWEEN 32 AND {DeviceToken.MaxTokenLength} AND token ~ '^[A-Za-z0-9_:.-]+$'");
                t.HasCheckConstraint("ck_device_tokens_token_sha256", "length(token_sha256) = 32");
                t.HasCheckConstraint("ck_device_tokens_app_version", "app_version IS NULL OR length(app_version) BETWEEN 1 AND 32");
                t.HasCheckConstraint("ck_device_tokens_locale", "locale IS NULL OR length(locale) BETWEEN 2 AND 35");
                t.HasCheckConstraint("ck_device_tokens_seen", "last_seen_at >= created_at");
            });
            device.HasKey(d => d.Id);
            device.Property(d => d.Id).ValueGeneratedNever();
            device.HasAlternateKey(d => new { d.TenantId, d.Id });
            device.Property(d => d.Platform).IsRequired();
            device.Property(d => d.Token).IsRequired();
            device.Property(d => d.TokenSha256).IsRequired();

            // One token belongs to one (tenant, user) at a time — globally; a new owner takes it over through
            // notifications.release_device_token (migration InitialNotifications).
            device.HasIndex(d => d.TokenSha256).IsUnique().HasDatabaseName(TokenUniqueIndex);
            device.HasIndex(d => new { d.TenantId, d.UserId, d.LastSeenAt });

            // Stale sweep (oldest first).
            device.HasIndex(d => d.LastSeenAt);
            device.HasQueryFilter(d => d.TenantId == CurrentTenantId);
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
internal sealed class NotificationsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<NotificationsDbContext>();
        NotificationsDbContext.Configure(builder, connectionString);
        return new NotificationsDbContext(builder.Options, new RequestContext());
    }
}
