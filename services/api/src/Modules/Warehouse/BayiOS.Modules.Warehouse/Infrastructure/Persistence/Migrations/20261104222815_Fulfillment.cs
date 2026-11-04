using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Fulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "picking_tasks",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assigned_to = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_picking_tasks", x => x.id);
                    table.UniqueConstraint("ak_picking_tasks_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_picking_tasks_status", "status IN ('open', 'in_progress', 'completed', 'completed_with_shortage', 'cancelled')");
                    table.CheckConstraint("ck_picking_tasks_type", "type IN ('pick', 'putaway')");
                    table.ForeignKey(
                        name: "fk_picking_tasks_warehouses_tenant_id_warehouse_id",
                        columns: x => new { x.tenant_id, x.warehouse_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "picking_task_lines",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    picking_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qty_to_pick = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    qty_picked = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    qty_short = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    shortage_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_picking_task_lines", x => x.id);
                    table.UniqueConstraint("ak_picking_task_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_picking_task_lines_quantities", "qty_to_pick > 0 AND qty_picked >= 0 AND qty_short >= 0 AND qty_picked + qty_short <= qty_to_pick");
                    table.CheckConstraint("ck_picking_task_lines_shortage_reason", "shortage_reason IS NULL OR shortage_reason IN ('not_found', 'damaged', 'insufficient')");
                    table.ForeignKey(
                        name: "fk_picking_task_lines_picking_tasks_tenant_id_picking_task_id",
                        columns: x => new { x.tenant_id, x.picking_task_id },
                        principalSchema: "warehouse",
                        principalTable: "picking_tasks",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_picking_task_lines_warehouse_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouse_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    picking_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    carrier = table.Column<string>(type: "text", nullable: true),
                    tracking_number = table.Column<string>(type: "text", nullable: true),
                    package_count = table.Column<int>(type: "integer", nullable: true),
                    packed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    packed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipments", x => x.id);
                    table.UniqueConstraint("ak_shipments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_shipments_confirmed", "status <> 'confirmed' OR (shipped_at IS NOT NULL AND confirmed_by IS NOT NULL)");
                    table.CheckConstraint("ck_shipments_package_count", "package_count IS NULL OR package_count > 0");
                    table.CheckConstraint("ck_shipments_status", "status IN ('draft', 'packed', 'confirmed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_shipments_picking_tasks_tenant_id_picking_task_id",
                        columns: x => new { x.tenant_id, x.picking_task_id },
                        principalSchema: "warehouse",
                        principalTable: "picking_tasks",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipments_warehouses_tenant_id_warehouse_id",
                        columns: x => new { x.tenant_id, x.warehouse_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipment_lines",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    picked_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    packed_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    short_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    shortage_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_lines", x => x.id);
                    table.UniqueConstraint("ak_shipment_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_shipment_lines_quantities", "picked_quantity > 0 AND quantity >= 0 AND short_quantity >= 0 AND quantity + short_quantity = picked_quantity AND (packed_quantity IS NULL OR (packed_quantity >= 0 AND packed_quantity <= quantity))");
                    table.CheckConstraint("ck_shipment_lines_shortage_reason", "shortage_reason IS NULL OR shortage_reason IN ('not_found', 'damaged', 'insufficient')");
                    table.ForeignKey(
                        name: "fk_shipment_lines_shipments_tenant_id_shipment_id",
                        columns: x => new { x.tenant_id, x.shipment_id },
                        principalSchema: "warehouse",
                        principalTable: "shipments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipment_lines_warehouse_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouse_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipment_packages",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_no = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_packages", x => x.id);
                    table.UniqueConstraint("ak_shipment_packages_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_shipment_packages_package_no", "package_no > 0");
                    table.ForeignKey(
                        name: "fk_shipment_packages_shipments_tenant_id_shipment_id",
                        columns: x => new { x.tenant_id, x.shipment_id },
                        principalSchema: "warehouse",
                        principalTable: "shipments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipment_package_items",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_package_items", x => x.id);
                    table.CheckConstraint("ck_shipment_package_items_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_shipment_package_items_shipment_lines_tenant_id_shipment_li",
                        columns: x => new { x.tenant_id, x.shipment_line_id },
                        principalSchema: "warehouse",
                        principalTable: "shipment_lines",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipment_package_items_shipment_packages_tenant_id_package_",
                        columns: x => new { x.tenant_id, x.package_id },
                        principalSchema: "warehouse",
                        principalTable: "shipment_packages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_picking_task_lines_tenant_id_location_id",
                schema: "warehouse",
                table: "picking_task_lines",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_picking_task_lines_tenant_id_order_line_id",
                schema: "warehouse",
                table: "picking_task_lines",
                columns: new[] { "tenant_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_picking_task_lines_tenant_id_picking_task_id_sequence",
                schema: "warehouse",
                table: "picking_task_lines",
                columns: new[] { "tenant_id", "picking_task_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_picking_tasks_tenant_id_assigned_to_status",
                schema: "warehouse",
                table: "picking_tasks",
                columns: new[] { "tenant_id", "assigned_to", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_picking_tasks_tenant_id_created_at_id",
                schema: "warehouse",
                table: "picking_tasks",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_picking_tasks_tenant_id_order_id",
                schema: "warehouse",
                table: "picking_tasks",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_picking_tasks_tenant_id_warehouse_id_status",
                schema: "warehouse",
                table: "picking_tasks",
                columns: new[] { "tenant_id", "warehouse_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_picking_tasks_tenant_id_order_id_active",
                schema: "warehouse",
                table: "picking_tasks",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "type = 'pick' AND status IN ('open', 'in_progress')");

            migrationBuilder.CreateIndex(
                name: "ix_shipment_lines_tenant_id_location_id",
                schema: "warehouse",
                table: "shipment_lines",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_lines_tenant_id_order_line_id",
                schema: "warehouse",
                table: "shipment_lines",
                columns: new[] { "tenant_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_lines_tenant_id_shipment_id",
                schema: "warehouse",
                table: "shipment_lines",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_package_items_tenant_id_package_id",
                schema: "warehouse",
                table: "shipment_package_items",
                columns: new[] { "tenant_id", "package_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_package_items_tenant_id_shipment_line_id",
                schema: "warehouse",
                table: "shipment_package_items",
                columns: new[] { "tenant_id", "shipment_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_packages_tenant_id_shipment_id_package_no",
                schema: "warehouse",
                table: "shipment_packages",
                columns: new[] { "tenant_id", "shipment_id", "package_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shipments_tenant_id_created_at_id",
                schema: "warehouse",
                table: "shipments",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_tenant_id_order_id",
                schema: "warehouse",
                table: "shipments",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_tenant_id_picking_task_id",
                schema: "warehouse",
                table: "shipments",
                columns: new[] { "tenant_id", "picking_task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_tenant_id_warehouse_id",
                schema: "warehouse",
                table: "shipments",
                columns: new[] { "tenant_id", "warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ux_shipments_tenant_id_number",
                schema: "warehouse",
                table: "shipments",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_shipments_tenant_id_order_id_open",
                schema: "warehouse",
                table: "shipments",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "status IN ('draft', 'packed')");

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the table owner. Tasks and shipments are
            // cancelled, never deleted; packages are written once by the pack check. ---
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON warehouse.picking_tasks, warehouse.picking_task_lines,
                    warehouse.shipments, warehouse.shipment_lines TO bayios_app;
                GRANT SELECT, INSERT ON warehouse.shipment_packages, warehouse.shipment_package_items TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
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
                    FOREACH t IN ARRAY ARRAY['picking_tasks', 'picking_task_lines', 'shipments', 'shipment_lines',
                                             'shipment_packages', 'shipment_package_items'] LOOP
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
                name: "picking_task_lines",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "shipment_package_items",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "shipment_lines",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "shipment_packages",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "shipments",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "picking_tasks",
                schema: "warehouse");
        }
    }
}
