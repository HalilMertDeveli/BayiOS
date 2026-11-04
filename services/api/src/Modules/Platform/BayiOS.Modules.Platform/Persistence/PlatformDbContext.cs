using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Platform.Persistence;

/// <summary>
/// Schema owner of the cross-cutting <c>platform</c> tables (system-architecture §5, ADR 0005). Used for migrations
/// only: the runtime code (BayiOS.SharedKernel) touches these tables with raw SQL on the request's UnitOfWork
/// connection, because it needs <c>INSERT … ON CONFLICT</c> semantics.
/// </summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<IdempotencyKeyRecord> IdempotencyKeys => Set<IdempotencyKeyRecord>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema))
            .UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<IdempotencyKeyRecord>(entity =>
        {
            entity.ToTable("idempotency_keys", t =>
            {
                t.HasCheckConstraint("ck_idempotency_keys_status", "status IN ('in_progress', 'completed')");
                t.HasCheckConstraint("ck_idempotency_keys_key", "char_length(key) BETWEEN 1 AND 100");
            });
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Id).ValueGeneratedNever();
            entity.Property(k => k.Key).IsRequired();
            entity.Property(k => k.Method).IsRequired();
            entity.Property(k => k.Path).IsRequired();
            entity.Property(k => k.RequestHash).IsRequired();
            entity.Property(k => k.Status).IsRequired();
            entity.Property(k => k.ResponseHeaders).HasColumnType("jsonb");

            // Key scope (api-strategy §7): (tenant_id, key) on tenant endpoints; (user_id, key) on tenant-less ones
            // such as POST /tenants, where no tenant exists yet.
            entity.HasIndex(k => new { k.TenantId, k.Key })
                .IsUnique()
                .HasFilter("tenant_id IS NOT NULL")
                .HasDatabaseName("ux_idempotency_keys_tenant_key");
            entity.HasIndex(k => new { k.UserId, k.Key })
                .IsUnique()
                .HasFilter("tenant_id IS NULL")
                .HasDatabaseName("ux_idempotency_keys_user_key");
            entity.HasIndex(k => k.ExpiresAt).HasDatabaseName("ix_idempotency_keys_expires_at");
        });
    }
}

/// <summary>Row of <c>platform.idempotency_keys</c> (data-model §5.12).</summary>
internal sealed class IdempotencyKeyRecord
{
    public Guid Id { get; init; }

    /// <summary>Null for tenant-less endpoints; the key is then scoped to <see cref="UserId"/>.</summary>
    public Guid? TenantId { get; init; }

    public Guid UserId { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    /// <summary>SHA-256 of method + path + request body.</summary>
    public byte[] RequestHash { get; init; } = [];

    public string Status { get; init; } = string.Empty;

    public int? ResponseStatus { get; init; }

    /// <summary>Replayed response headers (Content-Type, Location, ETag) as a JSON object.</summary>
    public string? ResponseHeaders { get; init; }

    /// <summary>Exact response bytes, so a replay is byte-identical (jsonb would reorder keys).</summary>
    public byte[]? ResponseBody { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// For <c>dotnet ef</c>. Applying migrations uses the schema-owner role: set <c>ConnectionStrings__Migrator</c>
/// (never the runtime <c>bayios_app</c> connection). Adding migrations needs no database.
/// </summary>
internal sealed class PlatformDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<PlatformDbContext>();
        PlatformDbContext.Configure(builder, connectionString);
        return new PlatformDbContext(builder.Options);
    }
}
