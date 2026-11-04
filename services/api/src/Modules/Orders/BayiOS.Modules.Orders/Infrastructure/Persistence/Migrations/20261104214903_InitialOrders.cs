using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Orders.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "order_number_sequences",
                schema: "orders",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_number_sequences", x => new { x.tenant_id, x.year });
                    table.CheckConstraint("ck_order_number_sequences_next_value", "next_value > 0");
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    fulfillment_status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    prices_include_tax = table.Column<bool>(type: "boolean", nullable: false),
                    price_date = table.Column<DateOnly>(type: "date", nullable: false),
                    requested_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.UniqueConstraint("ak_orders_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_orders_cancel_reason", "status <> 'cancelled' OR cancel_reason IS NOT NULL");
                    table.CheckConstraint("ck_orders_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_orders_fulfillment_status", "fulfillment_status IN ('none', 'partial', 'full')");
                    table.CheckConstraint("ck_orders_source", "source IN ('manual', 'whatsapp_ai', 'import', 'mobile')");
                    table.CheckConstraint("ck_orders_status", "status IN ('draft', 'pending_approval', 'approved', 'picking', 'packed', 'shipped', 'completed', 'cancelled')");
                    table.CheckConstraint("ck_orders_totals", "subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND grand_total >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    price_source = table.Column<string>(type: "text", nullable: true),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_overridden = table.Column<bool>(type: "boolean", nullable: false),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    discount_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    line_discount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    line_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    qty_reserved = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    qty_shipped = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    qty_cancelled = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => x.id);
                    table.UniqueConstraint("ak_order_lines_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_order_lines_amounts", "(unit_price IS NULL AND line_subtotal IS NULL AND line_discount IS NULL AND line_total IS NULL) OR (unit_price IS NOT NULL AND line_subtotal >= 0 AND line_discount >= 0 AND line_discount <= line_subtotal AND line_total = line_subtotal - line_discount)");
                    table.CheckConstraint("ck_order_lines_discount", "num_nonnulls(discount_percent, discount_amount) <= 1 AND (discount_percent IS NULL OR (discount_percent >= 0 AND discount_percent <= 100)) AND (discount_amount IS NULL OR discount_amount >= 0)");
                    table.CheckConstraint("ck_order_lines_fulfilment", "qty_reserved >= 0 AND qty_shipped >= 0 AND qty_cancelled >= 0 AND qty_reserved + qty_shipped + qty_cancelled <= quantity");
                    table.CheckConstraint("ck_order_lines_line_no", "line_no > 0");
                    table.CheckConstraint("ck_order_lines_quantity", "quantity > 0");
                    table.CheckConstraint("ck_order_lines_tax_rate", "tax_rate >= 0 AND tax_rate <= 100");
                    table.CheckConstraint("ck_order_lines_unit_price", "unit_price IS NULL OR unit_price >= 0");
                    table.ForeignKey(
                        name: "fk_order_lines_orders_tenant_id_order_id",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_shipments",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier = table.Column<string>(type: "text", nullable: true),
                    tracking_number = table.Column<string>(type: "text", nullable: true),
                    package_count = table.Column<int>(type: "integer", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    shipped_by = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_shipments", x => x.id);
                    table.UniqueConstraint("ak_order_shipments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_order_shipments_package_count", "package_count IS NULL OR package_count > 0");
                    table.ForeignKey(
                        name: "fk_order_shipments_orders_tenant_id_order_id",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    fulfillment_status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_action", "action IN ('create', 'submit', 'withdraw', 'reject', 'approve', 'start_picking', 'pack', 'ship', 'close_remaining', 'complete', 'cancel')");
                    table.CheckConstraint("ck_order_status_history_to_status", "to_status IN ('draft', 'pending_approval', 'approved', 'picking', 'packed', 'shipped', 'completed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_order_status_history_orders_tenant_id_order_id",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_shipment_lines",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_shipment_lines", x => x.id);
                    table.CheckConstraint("ck_order_shipment_lines_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_order_shipment_lines_order_lines_tenant_id_order_line_id",
                        columns: x => new { x.tenant_id, x.order_line_id },
                        principalSchema: "orders",
                        principalTable: "order_lines",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_shipment_lines_order_shipments_tenant_id_shipment_id",
                        columns: x => new { x.tenant_id, x.shipment_id },
                        principalSchema: "orders",
                        principalTable: "order_shipments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_tenant_id_order_id",
                schema: "orders",
                table: "order_lines",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_tenant_id_variant_id",
                schema: "orders",
                table: "order_lines",
                columns: new[] { "tenant_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_shipment_lines_tenant_id_order_line_id",
                schema: "orders",
                table: "order_shipment_lines",
                columns: new[] { "tenant_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_shipment_lines_tenant_id_shipment_id",
                schema: "orders",
                table: "order_shipment_lines",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_shipments_tenant_id_order_id_shipped_at",
                schema: "orders",
                table: "order_shipments",
                columns: new[] { "tenant_id", "order_id", "shipped_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_tenant_id_order_id_changed_at",
                schema: "orders",
                table: "order_status_history",
                columns: new[] { "tenant_id", "order_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_created_at_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_customer_id_created_at",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "customer_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_status_created_at_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_orders_tenant_id_number",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            // --- Line numbers: unique per order, but deferred to commit so a draft edit may renumber kept lines in
            // one statement batch. A constraint (not an index), so it lives outside the EF model. ---
            migrationBuilder.Sql("""
                ALTER TABLE orders.order_lines
                    ADD CONSTRAINT ux_order_lines_tenant_id_order_id_line_no
                    UNIQUE (tenant_id, order_id, line_no) DEFERRABLE INITIALLY DEFERRED;
                """);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. Orders are cancelled,
            // never deleted; draft lines may be deleted; history and shipments are append-only. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA orders FROM PUBLIC;
                GRANT USAGE ON SCHEMA orders TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON orders.orders, orders.order_number_sequences TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON orders.order_lines TO bayios_app;
                GRANT SELECT, INSERT ON orders.order_status_history, orders.order_shipments, orders.order_shipment_lines TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA orders FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA orders FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Only draft lines may be deleted (lines of submitted orders carry reservations and shipments). ---
            migrationBuilder.Sql("""
                CREATE FUNCTION orders.order_lines_delete_draft_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM orders.orders o
                               WHERE o.tenant_id = OLD.tenant_id AND o.id = OLD.order_id AND o.status <> 'draft') THEN
                        RAISE EXCEPTION 'order lines can only be deleted while the order is a draft'
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN OLD;
                END $$;

                CREATE TRIGGER trg_order_lines_delete_draft_only
                    BEFORE DELETE ON orders.order_lines
                    FOR EACH ROW EXECUTE FUNCTION orders.order_lines_delete_draft_only();
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['orders', 'order_lines', 'order_status_history', 'order_shipments',
                                             'order_shipment_lines', 'order_number_sequences'] LOOP
                        EXECUTE format('ALTER TABLE orders.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE orders.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON orders.%I FOR ALL TO bayios_app '
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
                name: "order_number_sequences",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_shipment_lines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_status_history",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_shipments",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS orders.order_lines_delete_draft_only();");
        }
    }
}
