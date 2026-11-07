using System.Data.Common;
using BayiOS.Modules.Integrations.Domain;
using BayiOS.Modules.Integrations.Parsing;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BayiOS.Modules.Integrations.Infrastructure.Persistence;

/// <summary>The <c>integrations</c> schema (data-model §5.10): AI parse requests and AI usage counters.</summary>
internal sealed class IntegrationsDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "integrations";

    private static readonly TenantGuardInterceptor TenantGuard = new();

    private readonly IRequestContext _requestContext;
    private readonly UnitOfWork? _unitOfWork;

    public IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options, IRequestContext requestContext, UnitOfWork? unitOfWork = null)
        : base(options)
    {
        _requestContext = requestContext;
        _unitOfWork = unitOfWork;
    }

    public DbSet<OrderParseRequest> ParseRequests => Set<OrderParseRequest>();

    public DbSet<AiUsageHour> Usage => Set<AiUsageHour>();

    public DbSet<AiTenantSettings> Settings => Set<AiTenantSettings>();

    public DbSet<LearnedAlias> Aliases => Set<LearnedAlias>();

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

        modelBuilder.Entity<OrderParseRequest>(request =>
        {
            request.ToTable("ai_parse_requests", t =>
            {
                t.HasCheckConstraint("ck_ai_parse_requests_status", $"status IN ({Quoted(OrderParseStatuses.All)})");
                t.HasCheckConstraint("ck_ai_parse_requests_channel", $"channel IN ({Quoted(OrderParseChannels.All)})");
                t.HasCheckConstraint("ck_ai_parse_requests_parser", $"parser IS NULL OR parser IN ({Quoted(ParserKinds.All)})");

                // Parsed (needs_review, approved, rejected) ⇔ the job recorded which parser and prompt it used.
                t.HasCheckConstraint(
                    "ck_ai_parse_requests_parsed",
                    "(status IN ('pending', 'failed')) = (parser IS NULL) AND (parser IS NULL) = (prompt_version IS NULL)");
                t.HasCheckConstraint(
                    "ck_ai_parse_requests_failure",
                    $"(status = 'failed') = (failure_code IS NOT NULL) AND (failure_code IS NULL OR failure_code IN ({Quoted(ParseFailureCodes.All)}))");
                t.HasCheckConstraint(
                    "ck_ai_parse_requests_fallback_reason", $"fallback_reason IS NULL OR fallback_reason IN ({Quoted(FallbackReasons.All)})");
                t.HasCheckConstraint("ck_ai_parse_requests_text_sha256", "text_sha256 ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint(
                    "ck_ai_parse_requests_counters",
                    "line_count >= 0 AND matched_count >= 0 AND ambiguous_count >= 0 AND unmatched_count >= 0 "
                    + "AND matched_count + ambiguous_count + unmatched_count = line_count "
                    + "AND input_tokens >= 0 AND output_tokens >= 0 AND cache_read_tokens >= 0 AND cache_write_tokens >= 0 AND duration_ms >= 0");
                t.HasCheckConstraint("ck_ai_parse_requests_confidence", "overall_confidence >= 0 AND overall_confidence <= 1");

                // Approved ⇔ a draft order exists; a purged row keeps no text at all.
                t.HasCheckConstraint("ck_ai_parse_requests_order", "(status = 'approved') = (order_id IS NOT NULL)");
                t.HasCheckConstraint(
                    "ck_ai_parse_requests_purged",
                    "text_purged_at IS NULL OR (raw_text IS NULL AND parsed_payload IS NULL AND review IS NULL)");
            });
            request.HasKey(r => r.Id);
            request.Property(r => r.Id).ValueGeneratedNever();
            request.HasAlternateKey(r => new { r.TenantId, r.Id });
            request.Property(r => r.RowVersion).IsConcurrencyToken();
            request.Property(r => r.Channel).IsRequired();
            request.Property(r => r.TextSha256).IsRequired().HasColumnType("char(64)");
            request.Property(r => r.Status).IsRequired();
            request.Property(r => r.ParsedPayload).HasColumnType("jsonb");
            request.Property(r => r.Review).HasColumnType("jsonb");
            request.Property(r => r.Warnings).IsRequired().HasColumnType("jsonb");
            request.Property(r => r.OverallConfidence).HasPrecision(4, 3);
            request.HasIndex(r => new { r.TenantId, r.CreatedAt }).IsDescending(false, true);
            request.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAt }).IsDescending(false, false, true);
            request.HasIndex(r => new { r.TenantId, r.TextSha256 });

            // Retention sweep (oldest first among rows that still hold text).
            request.HasIndex(r => r.CreatedAt).HasDatabaseName("ix_ai_parse_requests_unpurged_created_at").HasFilter("text_purged_at IS NULL");
            request.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AiUsageHour>(usage =>
        {
            usage.ToTable("ai_usage", t => t.HasCheckConstraint(
                "ck_ai_usage_counters",
                "parse_requests >= 0 AND ai_calls >= 0 AND ai_failures >= 0 AND fallbacks >= 0 AND input_tokens >= 0 "
                + "AND output_tokens >= 0 AND cache_read_tokens >= 0 AND cache_write_tokens >= 0"));
            usage.HasKey(u => new { u.TenantId, u.UsageHour });

            // Upserts name only the counter they increment; the others start at 0.
            usage.Property(u => u.ParseRequests).HasDefaultValue(0);
            usage.Property(u => u.AiCalls).HasDefaultValue(0);
            usage.Property(u => u.AiFailures).HasDefaultValue(0);
            usage.Property(u => u.Fallbacks).HasDefaultValue(0);
            usage.Property(u => u.InputTokens).HasDefaultValue(0L);
            usage.Property(u => u.OutputTokens).HasDefaultValue(0L);
            usage.Property(u => u.CacheReadTokens).HasDefaultValue(0L);
            usage.Property(u => u.CacheWriteTokens).HasDefaultValue(0L);
            usage.HasQueryFilter(u => u.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AiTenantSettings>(settings =>
        {
            settings.ToTable("ai_settings");
            settings.HasKey(s => s.TenantId);
            settings.Property(s => s.TenantId).ValueGeneratedNever();
            settings.Property(s => s.RowVersion).IsConcurrencyToken();
            settings.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<LearnedAlias>(alias =>
        {
            alias.ToTable("learned_aliases", t =>
            {
                t.HasCheckConstraint("ck_learned_aliases_phrase", "length(phrase) BETWEEN 1 AND 200 AND phrase = btrim(phrase)");
                t.HasCheckConstraint("ck_learned_aliases_confirmations", "confirmations >= 1");
            });
            alias.HasKey(a => a.Id);
            alias.Property(a => a.Id).ValueGeneratedNever();
            alias.HasAlternateKey(a => new { a.TenantId, a.Id });
            alias.Property(a => a.Phrase).IsRequired();

            // One decision per phrase and scope (a customer, or the whole tenant: NULL counts as one scope).
            alias.HasIndex(a => new { a.TenantId, a.CustomerId, a.Phrase }).IsUnique().AreNullsDistinct(false);
            alias.HasIndex(a => new { a.TenantId, a.VariantId });
            alias.HasQueryFilter(a => a.TenantId == CurrentTenantId);
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
internal sealed class IntegrationsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<IntegrationsDbContext>
{
    public IntegrationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=bayios;Username=bayios_migrator";
        var builder = new DbContextOptionsBuilder<IntegrationsDbContext>();
        IntegrationsDbContext.Configure(builder, connectionString);
        return new IntegrationsDbContext(builder.Options, new RequestContext());
    }
}
