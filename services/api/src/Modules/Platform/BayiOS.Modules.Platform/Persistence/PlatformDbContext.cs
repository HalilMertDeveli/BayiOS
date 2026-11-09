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

    /// <summary>Audit events live in their own schema (data-model §5.11); this context owns its migrations.</summary>
    public const string AuditSchema = "audit";

    public DbSet<IdempotencyKeyRecord> IdempotencyKeys => Set<IdempotencyKeyRecord>();

    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<OutboxMessageRecord> OutboxMessages => Set<OutboxMessageRecord>();

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

        modelBuilder.Entity<AuditEventRecord>(entity =>
        {
            entity.ToTable("audit_events", AuditSchema, t =>
            {
                t.HasCheckConstraint("ck_audit_events_action", "action ~ '^[a-z][a-z0-9_]*([.][a-z][a-z0-9_]*)+$' AND char_length(action) <= 100");
                t.HasCheckConstraint("ck_audit_events_entity_type", "char_length(entity_type) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_audit_events_user_agent", "user_agent IS NULL OR char_length(user_agent) <= 200");
                t.HasCheckConstraint("ck_audit_events_changes", "changes IS NULL OR pg_column_size(changes) <= 32768");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Action).IsRequired();
            entity.Property(e => e.EntityType).IsRequired();
            entity.Property(e => e.Changes).HasColumnType("jsonb");

            // Keyset pagination newest first, and the per-entity history (data-model §5.11).
            entity.HasIndex(e => new { e.TenantId, e.OccurredAt, e.Id })
                .IsDescending(false, true, true)
                .HasDatabaseName("ix_audit_events_tenant_occurred");
            entity.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId, e.OccurredAt })
                .IsDescending(false, false, false, true)
                .HasDatabaseName("ix_audit_events_tenant_entity");

            // Retention purge (data-policy §4, #89): oldest rows first across tenants.
            entity.HasIndex(e => e.OccurredAt).HasDatabaseName("ix_audit_events_occurred_at");
        });

        modelBuilder.Entity<JobRecord>(entity =>
        {
            entity.ToTable("jobs", t =>
            {
                t.HasCheckConstraint("ck_jobs_status", "status IN ('queued', 'running', 'succeeded', 'dead')");
                t.HasCheckConstraint("ck_jobs_type", "char_length(type) BETWEEN 1 AND 200");
                t.HasCheckConstraint("ck_jobs_attempts", "attempts >= 0 AND max_attempts BETWEEN 1 AND 100");
            });
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id).ValueGeneratedNever();
            entity.Property(j => j.Type).IsRequired();
            entity.Property(j => j.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(j => j.Status).IsRequired();

            // ADR 0006: claim order, lease sweep, and no double enqueue of a live job with the same dedupe key.
            entity.HasIndex(j => new { j.Priority, j.RunAfter })
                .IsDescending(true, false)
                .HasFilter("status = 'queued'")
                .HasDatabaseName("ix_jobs_queued");
            entity.HasIndex(j => j.LockedUntil)
                .HasFilter("status = 'running'")
                .HasDatabaseName("ix_jobs_running_lease");
            entity.HasIndex(j => new { j.Type, j.DedupeKey })
                .IsUnique()
                .HasFilter("dedupe_key IS NOT NULL AND status IN ('queued', 'running')")
                .HasDatabaseName("ux_jobs_type_dedupe_key");
            entity.HasIndex(j => j.FinishedAt)
                .HasFilter("status IN ('succeeded', 'dead')")
                .HasDatabaseName("ix_jobs_finished");
        });

        modelBuilder.Entity<OutboxMessageRecord>(entity =>
        {
            entity.ToTable("outbox_messages", t =>
                t.HasCheckConstraint("ck_outbox_messages_type", "char_length(type) BETWEEN 1 AND 200"));
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedNever();
            entity.Property(m => m.Type).IsRequired();
            entity.Property(m => m.Payload).IsRequired().HasColumnType("jsonb");

            entity.HasIndex(m => m.NextAttemptAt)
                .HasFilter("processed_at IS NULL")
                .HasDatabaseName("ix_outbox_messages_pending");
            entity.HasIndex(m => m.ProcessedAt)
                .HasFilter("processed_at IS NOT NULL")
                .HasDatabaseName("ix_outbox_messages_processed");
        });
    }
}

/// <summary>Row of <c>audit.audit_events</c> (data-model §5.11, threat-model §7). Append-only.</summary>
internal sealed class AuditEventRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    /// <summary>Null for system actors (background jobs).</summary>
    public Guid? ActorUserId { get; init; }

    public string? ActorRole { get; init; }

    /// <summary>Dotted action code, e.g. <c>tenant.settings_changed</c>.</summary>
    public string Action { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid? EntityId { get; init; }

    /// <summary>Minimal before/after summary; never secrets, PII only as "changed" markers (data-policy §8).</summary>
    public string? Changes { get; init; }

    /// <summary>HMAC-SHA-256 of the client IP with a server-side key (data-policy §8.5); null when no key is configured.</summary>
    public byte[]? IpHash { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Row of <c>platform.jobs</c> (ADR 0006). No RLS (data-model §2.3): the worker re-establishes the tenant.</summary>
internal sealed class JobRecord
{
    public Guid Id { get; init; }

    public Guid? TenantId { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Payload { get; init; } = "{}";

    /// <summary>queued → running → succeeded | queued (retry with backoff) | dead (attempts exhausted).</summary>
    public string Status { get; init; } = "queued";

    public short Priority { get; init; }

    public DateTimeOffset RunAfter { get; init; }

    public int Attempts { get; init; }

    public int MaxAttempts { get; init; }

    public string? LockedBy { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public string? LastError { get; init; }

    public string? DedupeKey { get; init; }

    public string? CorrelationId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>Row of <c>platform.outbox_messages</c> (ADR 0006). No RLS (data-model §2.3).</summary>
internal sealed class OutboxMessageRecord
{
    /// <summary>Message id; consumers are idempotent on it.</summary>
    public Guid Id { get; init; }

    public Guid? TenantId { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Payload { get; init; } = "{}";

    public string? CorrelationId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public DateTimeOffset NextAttemptAt { get; init; }

    public string? LastError { get; init; }
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
