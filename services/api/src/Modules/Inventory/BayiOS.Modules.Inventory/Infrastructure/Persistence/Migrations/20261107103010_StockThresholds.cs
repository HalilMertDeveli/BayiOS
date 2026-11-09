using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_thresholds",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: true),
                    min_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_thresholds", x => x.id);
                    table.CheckConstraint("ck_stock_thresholds_min_quantity", "min_quantity >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_thresholds_tenant_id_warehouse_id",
                schema: "inventory",
                table: "stock_thresholds",
                columns: new[] { "tenant_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_thresholds_key",
                schema: "inventory",
                table: "stock_thresholds",
                columns: new[] { "tenant_id", "variant_id", "warehouse_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // --- Privileges (data-model §2.1, §5.13). Runs as bayios_migrator, the table owner. Thresholds are
            // configuration: the app may delete them (removal is audited as inventory.threshold_removed). ---
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON inventory.stock_thresholds TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON inventory.stock_thresholds FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                ALTER TABLE inventory.stock_thresholds ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.stock_thresholds FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON inventory.stock_thresholds FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_thresholds",
                schema: "inventory");
        }
    }
}
