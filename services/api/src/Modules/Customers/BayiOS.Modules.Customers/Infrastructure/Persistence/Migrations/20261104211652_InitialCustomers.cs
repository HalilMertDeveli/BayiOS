using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "dealer_groups",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
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
                    table.PrimaryKey("pk_dealer_groups", x => x.id);
                    table.UniqueConstraint("ak_dealer_groups_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_dealer_groups_discount", "default_discount_percent IS NULL OR (default_discount_percent >= 0 AND default_discount_percent <= 100)");
                    table.CheckConstraint("ck_dealer_groups_status", "status IN ('active', 'archived')");
                });

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    contact_name = table.Column<string>(type: "text", nullable: true),
                    tax_number = table.Column<string>(type: "text", nullable: true),
                    tax_office = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    whatsapp_numbers = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    dealer_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_term_days = table.Column<int>(type: "integer", nullable: true),
                    credit_limit = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    anonymized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.UniqueConstraint("ak_customers_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_customers_credit_limit", "credit_limit IS NULL OR credit_limit >= 0");
                    table.CheckConstraint("ck_customers_currency", "currency IS NULL OR currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_customers_payment_term_days", "payment_term_days IS NULL OR payment_term_days BETWEEN 0 AND 3650");
                    table.CheckConstraint("ck_customers_status", "status IN ('active', 'inactive', 'archived')");
                    table.CheckConstraint("ck_customers_tax_number", "tax_number IS NULL OR tax_number ~ '^[0-9]{10,11}$'");
                    table.ForeignKey(
                        name: "fk_customers_dealer_groups_tenant_id_dealer_group_id",
                        columns: x => new { x.tenant_id, x.dealer_group_id },
                        principalSchema: "customers",
                        principalTable: "dealer_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_addresses",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    line1 = table.Column<string>(type: "text", nullable: false),
                    line2 = table.Column<string>(type: "text", nullable: true),
                    district = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: false),
                    postal_code = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "char(2)", nullable: false),
                    contact_name = table.Column<string>(type: "text", nullable: true),
                    contact_phone = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_addresses", x => x.id);
                    table.UniqueConstraint("ak_customer_addresses_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_customer_addresses_country", "country ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_customer_addresses_type", "type IN ('billing', 'shipping')");
                    table.ForeignKey(
                        name: "fk_customer_addresses_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_addresses_tenant_id_customer_id_created_at",
                schema: "customers",
                table: "customer_addresses",
                columns: new[] { "tenant_id", "customer_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_addresses_tenant_id_customer_id_type_default",
                schema: "customers",
                table: "customer_addresses",
                columns: new[] { "tenant_id", "customer_id", "type" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_dealer_group_id",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "dealer_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_name_id",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_tax_number",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "tax_number" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_whatsapp_numbers",
                schema: "customers",
                table: "customers",
                column: "whatsapp_numbers")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ux_customers_tenant_id_code",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            // --- Dealer group names: unique per tenant ignoring case among non-archived groups (data-model §5.5).
            // An expression index, so it lives outside the EF model; the application maps it by name. ---
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_dealer_groups_tenant_id_lower_name
                    ON customers.dealer_groups (tenant_id, lower(name)) WHERE status <> 'archived';
                """);

            // --- Search: pg_trgm GIN index backs "contains" (ILIKE '%q%') on the customer name. pg_trgm is a trusted
            // extension (the migrator may create it); the operator class is resolved from wherever it is installed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE s text;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
                        CREATE SCHEMA IF NOT EXISTS extensions;
                        CREATE EXTENSION pg_trgm WITH SCHEMA extensions;
                    END IF;
                    SELECT n.nspname INTO s FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_trgm';
                    EXECUTE format('CREATE INDEX ix_customers_name_trgm ON customers.customers USING gin (name %I.gin_trgm_ops)', s);
                END $$;
                """);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. Customers and dealer
            // groups are archived (customers anonymized on request, data-policy §5), never deleted; addresses may be. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA customers FROM PUBLIC;
                GRANT USAGE ON SCHEMA customers TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON customers.dealer_groups, customers.customers TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON customers.customer_addresses TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA customers FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA customers FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['dealer_groups', 'customers', 'customer_addresses'] LOOP
                        EXECUTE format('ALTER TABLE customers.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE customers.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON customers.%I FOR ALL TO bayios_app '
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
                name: "customer_addresses",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "dealer_groups",
                schema: "customers");
        }
    }
}
