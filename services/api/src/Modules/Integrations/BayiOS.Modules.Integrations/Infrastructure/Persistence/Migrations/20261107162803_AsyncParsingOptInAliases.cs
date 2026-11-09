using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AsyncParsingOptInAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_parse_requests_parser",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_parse_requests_status",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.AlterColumn<string>(
                name: "prompt_version",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "parser",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_code",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "warehouse_id",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_settings",
                schema: "integrations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_parsing_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_settings", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "learned_aliases",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    phrase = table.Column<string>(type: "text", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmations = table.Column<int>(type: "integer", nullable: false),
                    last_parse_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_learned_aliases", x => x.id);
                    table.UniqueConstraint("ak_learned_aliases_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_learned_aliases_confirmations", "confirmations >= 1");
                    table.CheckConstraint("ck_learned_aliases_phrase", "length(phrase) BETWEEN 1 AND 200 AND phrase = btrim(phrase)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_parse_requests_failure",
                schema: "integrations",
                table: "ai_parse_requests",
                sql: "(status = 'failed') = (failure_code IS NOT NULL) AND (failure_code IS NULL OR failure_code IN ('ai.parse_failed', 'ai.parse_timed_out', 'ai.opt_in_revoked'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_parse_requests_parsed",
                schema: "integrations",
                table: "ai_parse_requests",
                sql: "(status IN ('pending', 'failed')) = (parser IS NULL) AND (parser IS NULL) = (prompt_version IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_parse_requests_parser",
                schema: "integrations",
                table: "ai_parse_requests",
                sql: "parser IS NULL OR parser IN ('ai', 'rules')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_parse_requests_status",
                schema: "integrations",
                table: "ai_parse_requests",
                sql: "status IN ('pending', 'failed', 'needs_review', 'approved', 'rejected')");

            migrationBuilder.CreateIndex(
                name: "ix_learned_aliases_tenant_id_customer_id_phrase",
                schema: "integrations",
                table: "learned_aliases",
                columns: new[] { "tenant_id", "customer_id", "phrase" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_learned_aliases_tenant_id_variant_id",
                schema: "integrations",
                table: "learned_aliases",
                columns: new[] { "tenant_id", "variant_id" });

            // --- Privileges (data-model §2.1; runs as bayios_migrator, the owner). The opt-in is a tenant setting: read,
            // created and switched, never deleted. Learned aliases are deleted when a reviewer removes a wrong one. ---
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON integrations.ai_settings TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON integrations.learned_aliases TO bayios_app;
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON integrations.ai_settings FROM %I', r);
                            EXECUTE format('REVOKE ALL ON integrations.learned_aliases FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: the standard tenant isolation, forced (fail-closed without app.tenant_id). ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['ai_settings', 'learned_aliases'] LOOP
                        EXECUTE format('ALTER TABLE integrations.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE integrations.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON integrations.%I FOR ALL TO bayios_app '
                            'USING (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid) '
                            'WITH CHECK (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid)', t);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old schema cannot represent unparsed rows (parser/prompt_version NOT NULL): rolling back fails while
            // pending/failed parses exist — drain the queue and resolve them first (no data is dropped silently).

            migrationBuilder.DropTable(
                name: "ai_settings",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "learned_aliases",
                schema: "integrations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_parse_requests_failure",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_parse_requests_parsed",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_parse_requests_parser",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_parse_requests_status",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropColumn(
                name: "completed_at",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropColumn(
                name: "failure_code",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.DropColumn(
                name: "warehouse_id",
                schema: "integrations",
                table: "ai_parse_requests");

            migrationBuilder.AlterColumn<string>(
                name: "prompt_version",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "parser",
                schema: "integrations",
                table: "ai_parse_requests",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_parse_requests_parser",
                schema: "integrations",
                table: "ai_parse_requests",
                sql: "parser IN ('ai', 'rules')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_parse_requests_status",
                schema: "integrations",
                table: "ai_parse_requests",
                sql: "status IN ('needs_review', 'approved', 'rejected')");
        }
    }
}
