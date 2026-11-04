using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomFieldDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_field_definitions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    options = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    filterable = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_custom_field_definitions", x => x.id);
                    table.UniqueConstraint("ak_custom_field_definitions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_custom_field_definitions_data_type", "data_type IN ('text', 'number', 'integer', 'boolean', 'enum', 'multi_enum', 'range', 'date')");
                    table.CheckConstraint("ck_custom_field_definitions_entity_type", "entity_type IN ('product', 'variant')");
                    table.CheckConstraint("ck_custom_field_definitions_key", "key ~ '^[a-z][a-z0-9_]{0,49}$'");
                    table.CheckConstraint("ck_custom_field_definitions_options_array", "jsonb_typeof(options) = 'array'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_custom_field_definitions_tenant_id_entity_type_sort_order",
                schema: "catalog",
                table: "custom_field_definitions",
                columns: new[] { "tenant_id", "entity_type", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_custom_field_definitions_tenant_id_key",
                schema: "catalog",
                table: "custom_field_definitions",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            // --- Privileges (data-model §2.1): no DELETE, definitions are archived. Supabase client roles get nothing. ---
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON catalog.custom_field_definitions TO bayios_app;

                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON catalog.custom_field_definitions FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                ALTER TABLE catalog.custom_field_definitions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.custom_field_definitions FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON catalog.custom_field_definitions FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_field_definitions",
                schema: "catalog");
        }
    }
}
