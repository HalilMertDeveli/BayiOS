using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Warehouse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// #168: two-stage warehouse transfers (OD-01 — stock_transfers / stock_transfer_lines; the in-transit stock itself
    /// lives in the Inventory ledger, migration TransitStock) and stock counts (INV-06 — stock_counts /
    /// stock_count_lines; at most one open/submitted count per warehouse). Composite tenant FKs to warehouses and
    /// locations, ENABLE + FORCE RLS, <c>bayios_app</c> SELECT/INSERT/UPDATE (no DELETE).
    /// </summary>
    public partial class StockTransfersAndCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_counts",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    scope_location_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    scope_variant_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    is_blind = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_stock_counts", x => x.id);
                    table.UniqueConstraint("ak_stock_counts_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_stock_counts_applied", "status <> 'applied' OR (applied_at IS NOT NULL AND applied_by IS NOT NULL)");
                    table.CheckConstraint("ck_stock_counts_scope", "scope IN ('full', 'locations', 'variants')");
                    table.CheckConstraint("ck_stock_counts_status", "status IN ('open', 'submitted', 'applied', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_stock_counts_warehouses_tenant_id_warehouse_id",
                        columns: x => new { x.tenant_id, x.warehouse_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_transfers",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    source_warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dispatched_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_resolution = table.Column<string>(type: "text", nullable: true),
                    close_reason = table.Column<string>(type: "text", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_stock_transfers", x => x.id);
                    table.UniqueConstraint("ak_stock_transfers_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_stock_transfers_close_resolution", "(status = 'closed') = (close_resolution IS NOT NULL) AND (close_resolution IS NULL OR close_resolution IN ('lost', 'return_to_source'))");
                    table.CheckConstraint("ck_stock_transfers_dispatched", "status IN ('draft', 'cancelled') OR (dispatched_at IS NOT NULL AND dispatched_by IS NOT NULL)");
                    table.CheckConstraint("ck_stock_transfers_status", "status IN ('draft', 'in_transit', 'partially_received', 'received', 'closed', 'cancelled')");
                    table.CheckConstraint("ck_stock_transfers_warehouses", "source_warehouse_id <> destination_warehouse_id");
                    table.ForeignKey(
                        name: "fk_stock_transfers_warehouses_tenant_id_destination_warehouse_",
                        columns: x => new { x.tenant_id, x.destination_warehouse_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_transfers_warehouses_tenant_id_source_warehouse_id",
                        columns: x => new { x.tenant_id, x.source_warehouse_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_count_lines",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_count_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    counted_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    line_status = table.Column<string>(type: "text", nullable: false),
                    applied_delta = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_count_lines", x => x.id);
                    table.UniqueConstraint("ak_stock_count_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_stock_count_lines_quantities", "(counted_quantity IS NULL OR counted_quantity >= 0) AND (counted_quantity IS NULL) = (counted_at IS NULL) AND (line_status NOT IN ('counted', 'applied') OR counted_quantity IS NOT NULL)");
                    table.CheckConstraint("ck_stock_count_lines_status", "line_status IN ('pending', 'counted', 'applied', 'uncounted')");
                    table.ForeignKey(
                        name: "fk_stock_count_lines_stock_counts_tenant_id_stock_count_id",
                        columns: x => new { x.tenant_id, x.stock_count_id },
                        principalSchema: "warehouse",
                        principalTable: "stock_counts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_count_lines_warehouse_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouse_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_transfer_lines",
                schema: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    source_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    received_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    returned_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    lost_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_transfer_lines", x => x.id);
                    table.UniqueConstraint("ak_stock_transfer_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_stock_transfer_lines_quantities", "quantity > 0 AND received_quantity >= 0 AND returned_quantity >= 0 AND lost_quantity >= 0 AND received_quantity + returned_quantity + lost_quantity <= quantity");
                    table.ForeignKey(
                        name: "fk_stock_transfer_lines_stock_transfers_tenant_id_transfer_id",
                        columns: x => new { x.tenant_id, x.transfer_id },
                        principalSchema: "warehouse",
                        principalTable: "stock_transfers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_transfer_lines_warehouse_locations_tenant_id_destinat",
                        columns: x => new { x.tenant_id, x.destination_location_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouse_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_transfer_lines_warehouse_locations_tenant_id_source_l",
                        columns: x => new { x.tenant_id, x.source_location_id },
                        principalSchema: "warehouse",
                        principalTable: "warehouse_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_count_lines_tenant_id_location_id",
                schema: "warehouse",
                table: "stock_count_lines",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_count_lines_tenant_id_stock_count_id_variant_id_locat",
                schema: "warehouse",
                table: "stock_count_lines",
                columns: new[] { "tenant_id", "stock_count_id", "variant_id", "location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_stock_counts_tenant_id_created_at_id",
                schema: "warehouse",
                table: "stock_counts",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_counts_tenant_id_number",
                schema: "warehouse",
                table: "stock_counts",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stock_counts_tenant_id_warehouse_id_active",
                schema: "warehouse",
                table: "stock_counts",
                columns: new[] { "tenant_id", "warehouse_id" },
                unique: true,
                filter: "status IN ('open', 'submitted')");

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfer_lines_tenant_id_destination_location_id",
                schema: "warehouse",
                table: "stock_transfer_lines",
                columns: new[] { "tenant_id", "destination_location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfer_lines_tenant_id_source_location_id",
                schema: "warehouse",
                table: "stock_transfer_lines",
                columns: new[] { "tenant_id", "source_location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfer_lines_tenant_id_transfer_id_line_no",
                schema: "warehouse",
                table: "stock_transfer_lines",
                columns: new[] { "tenant_id", "transfer_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfer_lines_tenant_id_transfer_id_variant_id_sourc",
                schema: "warehouse",
                table: "stock_transfer_lines",
                columns: new[] { "tenant_id", "transfer_id", "variant_id", "source_location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfers_tenant_id_created_at_id",
                schema: "warehouse",
                table: "stock_transfers",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfers_tenant_id_destination_warehouse_id_status",
                schema: "warehouse",
                table: "stock_transfers",
                columns: new[] { "tenant_id", "destination_warehouse_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfers_tenant_id_source_warehouse_id_status",
                schema: "warehouse",
                table: "stock_transfers",
                columns: new[] { "tenant_id", "source_warehouse_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfers_tenant_id_status_created_at",
                schema: "warehouse",
                table: "stock_transfers",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_transfers_tenant_id_number",
                schema: "warehouse",
                table: "stock_transfers",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the table owner. Transfers and counts are
            // cancelled or closed, never deleted; their lines only change quantities and status. ---
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON warehouse.stock_transfers, warehouse.stock_transfer_lines,
                    warehouse.stock_counts, warehouse.stock_count_lines TO bayios_app;

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
                    FOREACH t IN ARRAY ARRAY['stock_transfers', 'stock_transfer_lines', 'stock_counts', 'stock_count_lines'] LOOP
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
                name: "stock_count_lines",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "stock_transfer_lines",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "stock_counts",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "stock_transfers",
                schema: "warehouse");
        }
    }
}
