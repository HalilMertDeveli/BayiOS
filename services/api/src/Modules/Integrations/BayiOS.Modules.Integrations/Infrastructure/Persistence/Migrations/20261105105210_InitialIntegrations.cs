using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.CreateTable(
                name: "ai_parse_requests",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raw_text = table.Column<string>(type: "text", nullable: true),
                    text_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    text_length = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    parser = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    fallback_reason = table.Column<string>(type: "text", nullable: true),
                    parsed_payload = table.Column<string>(type: "jsonb", nullable: true),
                    review = table.Column<string>(type: "jsonb", nullable: true),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    matched_count = table.Column<int>(type: "integer", nullable: false),
                    ambiguous_count = table.Column<int>(type: "integer", nullable: false),
                    unmatched_count = table.Column<int>(type: "integer", nullable: false),
                    overall_confidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_read_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_write_tokens = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    text_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_parse_requests", x => x.id);
                    table.UniqueConstraint("ak_ai_parse_requests_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_ai_parse_requests_channel", "channel IN ('whatsapp_paste')");
                    table.CheckConstraint("ck_ai_parse_requests_confidence", "overall_confidence >= 0 AND overall_confidence <= 1");
                    table.CheckConstraint("ck_ai_parse_requests_counters", "line_count >= 0 AND matched_count >= 0 AND ambiguous_count >= 0 AND unmatched_count >= 0 AND matched_count + ambiguous_count + unmatched_count = line_count AND input_tokens >= 0 AND output_tokens >= 0 AND cache_read_tokens >= 0 AND cache_write_tokens >= 0 AND duration_ms >= 0");
                    table.CheckConstraint("ck_ai_parse_requests_fallback_reason", "fallback_reason IS NULL OR fallback_reason IN ('disabled', 'quota_exceeded', 'provider_error', 'timeout', 'refused', 'truncated', 'invalid_output')");
                    table.CheckConstraint("ck_ai_parse_requests_order", "(status = 'approved') = (order_id IS NOT NULL)");
                    table.CheckConstraint("ck_ai_parse_requests_parser", "parser IN ('ai', 'rules')");
                    table.CheckConstraint("ck_ai_parse_requests_purged", "text_purged_at IS NULL OR (raw_text IS NULL AND parsed_payload IS NULL AND review IS NULL)");
                    table.CheckConstraint("ck_ai_parse_requests_status", "status IN ('needs_review', 'approved', 'rejected')");
                    table.CheckConstraint("ck_ai_parse_requests_text_sha256", "text_sha256 ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "ai_usage",
                schema: "integrations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    usage_hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    parse_requests = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ai_calls = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ai_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    fallbacks = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage", x => new { x.tenant_id, x.usage_hour });
                    table.CheckConstraint("ck_ai_usage_counters", "parse_requests >= 0 AND ai_calls >= 0 AND ai_failures >= 0 AND fallbacks >= 0 AND input_tokens >= 0 AND output_tokens >= 0 AND cache_read_tokens >= 0 AND cache_write_tokens >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_parse_requests_tenant_id_created_at",
                schema: "integrations",
                table: "ai_parse_requests",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ai_parse_requests_tenant_id_status_created_at",
                schema: "integrations",
                table: "ai_parse_requests",
                columns: new[] { "tenant_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ai_parse_requests_tenant_id_text_sha256",
                schema: "integrations",
                table: "ai_parse_requests",
                columns: new[] { "tenant_id", "text_sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_parse_requests_unpurged_created_at",
                schema: "integrations",
                table: "ai_parse_requests",
                column: "created_at",
                filter: "text_purged_at IS NULL");

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner.
            // Parse requests are history: never deleted by the app (the text is cleared after retention through the
            // definer function below). Usage counters are upserted, never deleted. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA integrations FROM PUBLIC;
                GRANT USAGE ON SCHEMA integrations TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON integrations.ai_parse_requests TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON integrations.ai_usage TO bayios_app;
                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA integrations FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA integrations FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['ai_parse_requests', 'ai_usage'] LOOP
                        EXECUTE format('ALTER TABLE integrations.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE integrations.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON integrations.%I FOR ALL TO bayios_app '
                            'USING (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid) '
                            'WITH CHECK (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid)', t);
                    END LOOP;
                END $$;
                """);

            // --- Text retention (data-policy §4: the message text is personal data, 30 days by default). A tenant-less
            // job sees nothing under RLS, so the purge runs as the table owner through a SECURITY DEFINER function with a
            // pinned search_path. The owner's policies expose only rows older than one day, and the UPDATE check only
            // lets the text columns become NULL (with text_purged_at set): nothing else of the row can change. ---
            migrationBuilder.Sql("""
                CREATE POLICY ai_parse_requests_purge_select ON integrations.ai_parse_requests FOR SELECT TO bayios_migrator
                    USING (created_at < now() - interval '1 day');
                CREATE POLICY ai_parse_requests_purge_update ON integrations.ai_parse_requests FOR UPDATE TO bayios_migrator
                    USING (created_at < now() - interval '1 day')
                    WITH CHECK (raw_text IS NULL AND parsed_payload IS NULL AND review IS NULL AND text_purged_at IS NOT NULL);

                CREATE FUNCTION integrations.purge_expired_parse_texts(p_retention interval, p_limit integer) RETURNS integer
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    purged integer;
                BEGIN
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;
                    IF p_retention IS NULL OR p_retention < interval '1 day' THEN
                        RAISE EXCEPTION 'p_retention must be at least 1 day';
                    END IF;

                    UPDATE integrations.ai_parse_requests r
                    SET raw_text = NULL, parsed_payload = NULL, review = NULL, text_purged_at = now()
                    WHERE r.id IN (
                        SELECT x.id FROM integrations.ai_parse_requests x
                        WHERE x.text_purged_at IS NULL AND x.created_at < now() - p_retention
                        ORDER BY x.created_at
                        LIMIT p_limit);
                    GET DIAGNOSTICS purged = ROW_COUNT;
                    RETURN purged;
                END;
                $$;

                REVOKE ALL ON FUNCTION integrations.purge_expired_parse_texts(interval, integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION integrations.purge_expired_parse_texts(interval, integer) TO bayios_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS integrations.purge_expired_parse_texts(interval, integer);");

            migrationBuilder.DropTable(
                name: "ai_parse_requests",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "ai_usage",
                schema: "integrations");
        }
    }
}
