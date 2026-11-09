using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "brands",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_brands", x => x.id);
                    table.UniqueConstraint("ak_brands_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.UniqueConstraint("ak_categories_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_categories_depth", "depth BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "fk_categories_categories_tenant_id_parent_id",
                        columns: x => new { x.tenant_id, x.parent_id },
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "units",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    allows_fraction = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_units", x => x.id);
                    table.UniqueConstraint("ak_units_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
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
                    table.PrimaryKey("pk_products", x => x.id);
                    table.UniqueConstraint("ak_products_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_products_attributes_object", "jsonb_typeof(attributes) = 'object'");
                    table.CheckConstraint("ck_products_status", "status IN ('active', 'inactive', 'archived')");
                    table.CheckConstraint("ck_products_tax_rate", "tax_rate BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_products_brands_tenant_id_brand_id",
                        columns: x => new { x.tenant_id, x.brand_id },
                        principalSchema: "catalog",
                        principalTable: "brands",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_categories_tenant_id_category_id",
                        columns: x => new { x.tenant_id, x.category_id },
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_units_tenant_id_default_unit_id",
                        columns: x => new { x.tenant_id, x.default_unit_id },
                        principalSchema: "catalog",
                        principalTable: "units",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    barcode = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    option_values = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
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
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.UniqueConstraint("ak_product_variants_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_product_variants_attributes_object", "jsonb_typeof(attributes) = 'object'");
                    table.CheckConstraint("ck_product_variants_option_values_object", "jsonb_typeof(option_values) = 'object'");
                    table.CheckConstraint("ck_product_variants_status", "status IN ('active', 'inactive', 'archived')");
                    table.ForeignKey(
                        name: "fk_product_variants_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_variants_units_tenant_id_unit_id",
                        columns: x => new { x.tenant_id, x.unit_id },
                        principalSchema: "catalog",
                        principalTable: "units",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id_parent_id",
                schema: "catalog",
                table: "categories",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_tenant_id_product_id",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_tenant_id_unit_id",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_product_variants_tenant_id_barcode",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "barcode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_variants_tenant_id_sku",
                schema: "catalog",
                table: "product_variants",
                columns: new[] { "tenant_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_brand_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "brand_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_category_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_created_at_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_default_unit_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "default_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_name_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_status_name",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "status", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_units_tenant_id_code",
                schema: "catalog",
                table: "units",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            // --- Indexes EF cannot express (data-model §5.3). ---
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_brands_tenant_id_lower_name
                    ON catalog.brands (tenant_id, lower(name)) WHERE archived_at IS NULL;
                CREATE UNIQUE INDEX ux_categories_tenant_id_parent_id_lower_name
                    ON catalog.categories (tenant_id, parent_id, lower(name)) NULLS NOT DISTINCT WHERE archived_at IS NULL;
                CREATE INDEX ix_categories_tenant_id_path
                    ON catalog.categories (tenant_id, path text_pattern_ops);
                CREATE INDEX ix_products_attributes
                    ON catalog.products USING gin (attributes jsonb_path_ops);
                CREATE INDEX ix_product_variants_attributes
                    ON catalog.product_variants USING gin (attributes jsonb_path_ops);
                """);

            // --- Search: pg_trgm GIN indexes back "contains" (ILIKE '%q%') on product name and SKU.
            // pg_trgm is a trusted extension (the migrator may create it); on Supabase it already lives in
            // `extensions`. The operator class is resolved from wherever the extension is installed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE s text;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
                        CREATE SCHEMA IF NOT EXISTS extensions;
                        CREATE EXTENSION pg_trgm WITH SCHEMA extensions;
                    END IF;
                    SELECT n.nspname INTO s FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_trgm';
                    EXECUTE format('CREATE INDEX ix_products_name_trgm ON catalog.products USING gin (name %I.gin_trgm_ops)', s);
                    EXECUTE format('CREATE INDEX ix_product_variants_sku_trgm ON catalog.product_variants USING gin (sku %I.gin_trgm_ops)', s);
                END $$;
                """);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner.
            // No DELETE: catalog rows are archived, never physically deleted (PRD CAT-02). ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA catalog FROM PUBLIC;
                GRANT USAGE ON SCHEMA catalog TO bayios_app;
                GRANT SELECT, INSERT, UPDATE
                    ON catalog.units, catalog.brands, catalog.categories, catalog.products, catalog.product_variants
                    TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA catalog FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA catalog FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['units', 'brands', 'categories', 'products', 'product_variants'] LOOP
                        EXECUTE format('ALTER TABLE catalog.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE catalog.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON catalog.%I FOR ALL TO bayios_app '
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
                name: "product_variants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "brands",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "units",
                schema: "catalog");
        }
    }
}
