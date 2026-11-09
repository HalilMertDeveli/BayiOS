using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWarehouse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "warehouse");

            migrationBuilder.CreateTable(
                name: "warehouses",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    uses_locations = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouses", x => x.id);
                    table.UniqueConstraint("ak_warehouses_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_warehouses_status", "status IN ('active', 'inactive')");
                });

            migrationBuilder.CreateTable(
                name: "warehouse_locations",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    barcode = table.Column<string>(type: "text", nullable: true),
                    pick_sequence = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse_locations", x => x.id);
                    table.UniqueConstraint("ak_warehouse_locations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_warehouse_locations_warehouses_tenant_id_warehouse_id",
                        columns: x => new { x.tenant_id, x.warehouse_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_warehouse_locations_tenant_id_warehouse_id_pick_sequence",
                schema: "warehouse",
                table: "warehouse_locations",
                columns: new[] { "tenant_id", "warehouse_id", "pick_sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_warehouse_locations_tenant_id_barcode",
                schema: "warehouse",
                table: "warehouse_locations",
                columns: new[] { "tenant_id", "barcode" },
                unique: true,
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_warehouse_locations_tenant_id_warehouse_id_code",
                schema: "warehouse",
                table: "warehouse_locations",
                columns: new[] { "tenant_id", "warehouse_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warehouses_tenant_id_code",
                schema: "warehouse",
                table: "warehouses",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warehouses_tenant_id_default",
                schema: "warehouse",
                table: "warehouses",
                column: "tenant_id",
                unique: true,
                filter: "is_default");

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner.
            // No DELETE: warehouses and locations are deactivated, never physically deleted. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA warehouse FROM PUBLIC;
                GRANT USAGE ON SCHEMA warehouse TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON warehouse.warehouses, warehouse.warehouse_locations TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA warehouse FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA warehouse FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['warehouses', 'warehouse_locations'] LOOP
                        EXECUTE format('ALTER TABLE warehouse.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE warehouse.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON warehouse.%I FOR ALL TO bayios_app '
                            'USING (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid) '
                            'WITH CHECK (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid)', t);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouse_locations",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "warehouses",
                schema: "warehouse");
        }
    }
}
