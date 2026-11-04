using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_headers = table.Column<string>(type: "jsonb", nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.id);
                    table.CheckConstraint("ck_idempotency_keys_key", "char_length(key) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_idempotency_keys_status", "status IN ('in_progress', 'completed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires_at",
                schema: "platform",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_keys_tenant_key",
                schema: "platform",
                table: "idempotency_keys",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "tenant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_keys_user_key",
                schema: "platform",
                table: "idempotency_keys",
                columns: new[] { "user_id", "key" },
                unique: true,
                filter: "tenant_id IS NULL");

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA platform FROM PUBLIC;
                GRANT USAGE ON SCHEMA platform TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON platform.idempotency_keys TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA platform FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA platform FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security (data-model §2.3): tenant keys are visible in their tenant context; tenant-less
            // keys (e.g. POST /tenants) only to their user. Rows are always written as the calling user. ---
            migrationBuilder.Sql("""
                ALTER TABLE platform.idempotency_keys ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.idempotency_keys FORCE ROW LEVEL SECURITY;

                CREATE POLICY idempotency_scope ON platform.idempotency_keys FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        OR (tenant_id IS NULL AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid))
                    WITH CHECK (user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                        AND (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "platform");
        }
    }
}
