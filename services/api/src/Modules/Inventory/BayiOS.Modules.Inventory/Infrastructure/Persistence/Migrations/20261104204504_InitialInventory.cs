using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "inventory_balances",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_hand = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    reserved = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    allow_negative = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_balances", x => x.id);
                    table.CheckConstraint("ck_inventory_balances_no_negative", "allow_negative OR (on_hand >= 0 AND reserved <= on_hand)");
                    table.CheckConstraint("ck_inventory_balances_reserved_nonnegative", "reserved >= 0");
                });

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transfer_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    balance_after = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_movements", x => x.id);
                    table.CheckConstraint("ck_inventory_movements_quantity_nonzero", "quantity <> 0");
                    table.CheckConstraint("ck_inventory_movements_quantity_sign", "(type IN ('receipt', 'adjustment_in', 'transfer_in', 'return_in') AND quantity > 0) OR (type IN ('adjustment_out', 'transfer_out', 'shipment_out', 'damage_out') AND quantity < 0) OR type = 'count_correction'");
                    table.CheckConstraint("ck_inventory_movements_source_type", "source_type IN ('receipt', 'adjustment', 'stock_count', 'transfer', 'shipment', 'return', 'import')");
                    table.CheckConstraint("ck_inventory_movements_type", "type IN ('receipt', 'adjustment_in', 'adjustment_out', 'count_correction', 'transfer_out', 'transfer_in', 'shipment_out', 'return_in', 'damage_out')");
                    table.CheckConstraint("ck_inventory_movements_unit_cost", "unit_cost IS NULL OR unit_cost >= 0");
                });

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    consumed_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    released_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 0m),
                    status = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservations", x => x.id);
                    table.CheckConstraint("ck_stock_reservations_closed", "status = 'active' OR consumed_quantity + released_quantity = quantity");
                    table.CheckConstraint("ck_stock_reservations_progress", "consumed_quantity >= 0 AND released_quantity >= 0 AND consumed_quantity + released_quantity <= quantity");
                    table.CheckConstraint("ck_stock_reservations_quantity", "quantity > 0");
                    table.CheckConstraint("ck_stock_reservations_status", "status IN ('active', 'consumed', 'released')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_balances_tenant_id_warehouse_id_variant_id",
                schema: "inventory",
                table: "inventory_balances",
                columns: new[] { "tenant_id", "warehouse_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_balances_key",
                schema: "inventory",
                table: "inventory_balances",
                columns: new[] { "tenant_id", "variant_id", "warehouse_id", "location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movements_tenant_id_source_type_source_id",
                schema: "inventory",
                table: "inventory_movements",
                columns: new[] { "tenant_id", "source_type", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movements_tenant_id_variant_id_occurred_at_id",
                schema: "inventory",
                table: "inventory_movements",
                columns: new[] { "tenant_id", "variant_id", "occurred_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movements_tenant_id_variant_id_warehouse_id_occur",
                schema: "inventory",
                table: "inventory_movements",
                columns: new[] { "tenant_id", "variant_id", "warehouse_id", "occurred_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_movements_source_line",
                schema: "inventory",
                table: "inventory_movements",
                columns: new[] { "tenant_id", "source_type", "source_line_id", "type", "location_id" },
                unique: true,
                filter: "source_line_id IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_tenant_id_order_id",
                schema: "inventory",
                table: "stock_reservations",
                columns: new[] { "tenant_id", "order_id" },
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_tenant_id_variant_id_warehouse_id",
                schema: "inventory",
                table: "stock_reservations",
                columns: new[] { "tenant_id", "variant_id", "warehouse_id" },
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ux_stock_reservations_active_line",
                schema: "inventory",
                table: "stock_reservations",
                columns: new[] { "tenant_id", "order_line_id", "location_id" },
                unique: true,
                filter: "status = 'active'")
                .Annotation("Npgsql:NullsDistinct", false);

            // --- Append-only ledger (ADR 0004 §1, invariant I4). Beyond the missing grants, a trigger rejects
            // UPDATE/DELETE/TRUNCATE for every role, the owner included. ---
            migrationBuilder.Sql("""
                CREATE FUNCTION inventory.reject_ledger_change() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog
                AS $$
                BEGIN
                    RAISE EXCEPTION 'inventory.inventory_movements is append-only (% rejected)', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_inventory_movements_append_only
                    BEFORE UPDATE OR DELETE ON inventory.inventory_movements
                    FOR EACH ROW EXECUTE FUNCTION inventory.reject_ledger_change();
                CREATE TRIGGER trg_inventory_movements_no_truncate
                    BEFORE TRUNCATE ON inventory.inventory_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION inventory.reject_ledger_change();
                """);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner.
            // Ledger: SELECT/INSERT only. Balances and reservations: no DELETE (rows are never removed). ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA inventory FROM PUBLIC;
                REVOKE ALL ON FUNCTION inventory.reject_ledger_change() FROM PUBLIC;
                GRANT USAGE ON SCHEMA inventory TO bayios_app;
                GRANT SELECT, INSERT ON inventory.inventory_movements TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON inventory.inventory_balances, inventory.stock_reservations TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA inventory FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA inventory FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['inventory_movements', 'inventory_balances', 'stock_reservations'] LOOP
                        EXECUTE format('ALTER TABLE inventory.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE inventory.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON inventory.%I FOR ALL TO bayios_app '
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
                name: "inventory_balances",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "inventory_movements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_reservations",
                schema: "inventory");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS inventory.reject_ledger_change();");
        }
    }
}
