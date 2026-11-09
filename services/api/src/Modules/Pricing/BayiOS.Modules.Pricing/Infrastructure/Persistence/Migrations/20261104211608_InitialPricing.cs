using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Pricing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pricing");

            migrationBuilder.CreateTable(
                name: "price_lists",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    prices_include_tax = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    base_price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_lists", x => x.id);
                    table.UniqueConstraint("ak_price_lists_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_price_lists_base_rule", "(base_price_list_id IS NULL) = (discount_percent IS NULL) AND base_price_list_id IS DISTINCT FROM id");
                    table.CheckConstraint("ck_price_lists_base_sales", "base_price_list_id IS NULL OR kind = 'sales'");
                    table.CheckConstraint("ck_price_lists_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_price_lists_default_sales", "NOT is_default OR (kind = 'sales' AND status = 'active')");
                    table.CheckConstraint("ck_price_lists_discount", "discount_percent IS NULL OR (discount_percent >= 0 AND discount_percent <= 100)");
                    table.CheckConstraint("ck_price_lists_kind", "kind IN ('sales', 'purchase')");
                    table.CheckConstraint("ck_price_lists_status", "status IN ('active', 'inactive', 'archived')");
                    table.CheckConstraint("ck_price_lists_validity", "valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from");
                    table.ForeignKey(
                        name: "fk_price_lists_price_lists_tenant_id_base_price_list_id",
                        columns: x => new { x.tenant_id, x.base_price_list_id },
                        principalSchema: "pricing",
                        principalTable: "price_lists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_list_items",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false, defaultValue: 1m),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_list_items", x => x.id);
                    table.UniqueConstraint("ak_price_list_items_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_price_list_items_min_quantity", "min_quantity > 0");
                    table.CheckConstraint("ck_price_list_items_unit_price", "unit_price >= 0");
                    table.ForeignKey(
                        name: "fk_price_list_items_price_lists_tenant_id_price_list_id",
                        columns: x => new { x.tenant_id, x.price_list_id },
                        principalSchema: "pricing",
                        principalTable: "price_lists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_price_list_items_tenant_id_variant_id",
                schema: "pricing",
                table: "price_list_items",
                columns: new[] { "tenant_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ux_price_list_items_tenant_id_list_id_variant_id_min_qty",
                schema: "pricing",
                table: "price_list_items",
                columns: new[] { "tenant_id", "price_list_id", "variant_id", "min_quantity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_lists_tenant_id_base_price_list_id",
                schema: "pricing",
                table: "price_lists",
                columns: new[] { "tenant_id", "base_price_list_id" });

            migrationBuilder.CreateIndex(
                name: "ux_price_lists_tenant_id_currency_default",
                schema: "pricing",
                table: "price_lists",
                columns: new[] { "tenant_id", "currency" },
                unique: true,
                filter: "is_default AND kind = 'sales'");

            migrationBuilder.CreateIndex(
                name: "ux_price_lists_tenant_id_name",
                schema: "pricing",
                table: "price_lists",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "status <> 'archived'");

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner.
            // Price lists are archived, never deleted; items are plain configuration rows and may be deleted. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA pricing FROM PUBLIC;
                GRANT USAGE ON SCHEMA pricing TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON pricing.price_lists TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON pricing.price_list_items TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA pricing FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA pricing FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['price_lists', 'price_list_items'] LOOP
                        EXECUTE format('ALTER TABLE pricing.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE pricing.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON pricing.%I FOR ALL TO bayios_app '
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
                name: "price_list_items",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "price_lists",
                schema: "pricing");
        }
    }
}
