using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Reports.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reports");

            migrationBuilder.CreateTable(
                name: "export_requests",
                schema: "reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_requests", x => x.id);
                    table.CheckConstraint("ck_export_requests_format", "format IN ('csv', 'xlsx')");
                    table.CheckConstraint("ck_export_requests_report", "report IN ('critical_stock', 'orders', 'top_products', 'stock_movements', 'picking_tasks', 'ai_usage')");
                    table.CheckConstraint("ck_export_requests_row_count", "row_count >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_export_requests_tenant_id_created_at",
                schema: "reports",
                table: "export_requests",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. The export log only
            // backs the hourly export limit: rows are inserted and read, never updated; the next export of the tenant
            // deletes its own rows older than a day (under RLS), so the table stays small. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA reports FROM PUBLIC;
                GRANT USAGE ON SCHEMA reports TO bayios_app;
                GRANT SELECT, INSERT, DELETE ON reports.export_requests TO bayios_app;
                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA reports FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA reports FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                ALTER TABLE reports.export_requests ENABLE ROW LEVEL SECURITY;
                ALTER TABLE reports.export_requests FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON reports.export_requests FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "export_requests",
                schema: "reports");
        }
    }
}
