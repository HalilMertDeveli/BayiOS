using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    sector = table.Column<string>(type: "text", nullable: false),
                    default_currency = table.Column<string>(type: "char(3)", nullable: false),
                    locale = table.Column<string>(type: "text", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_sector", "sector IN ('motorcycle_parts', 'led_electronics', 'general')");
                    table.CheckConstraint("ck_tenants_status", "status IN ('active', 'suspended', 'closed')");
                });

            migrationBuilder.CreateTable(
                name: "user_profiles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    full_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profiles", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.CheckConstraint("ck_memberships_role", "role IN ('owner', 'admin', 'manager', 'sales', 'warehouse_staff', 'viewer')");
                    table.CheckConstraint("ck_memberships_status", "status IN ('invited', 'active', 'disabled')");
                    table.ForeignKey(
                        name: "fk_memberships_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                schema: "identity",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    negative_stock_policy = table.Column<string>(type: "text", nullable: false),
                    sales_can_approve_orders = table.Column<bool>(type: "boolean", nullable: false),
                    sales_max_discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    show_cost_to_manager = table.Column<bool>(type: "boolean", nullable: false),
                    show_cost_to_viewer = table.Column<bool>(type: "boolean", nullable: false),
                    staff_can_assign_barcode = table.Column<bool>(type: "boolean", nullable: false),
                    staff_can_approve_receipt = table.Column<bool>(type: "boolean", nullable: false),
                    require2fa = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_settings", x => x.tenant_id);
                    table.CheckConstraint("ck_tenant_settings_negative_stock_policy", "negative_stock_policy IN ('block', 'warn', 'allow')");
                    table.CheckConstraint("ck_tenant_settings_sales_max_discount", "sales_max_discount_percent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_tenant_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_user_id",
                schema: "identity",
                table: "memberships",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id_status",
                schema: "identity",
                table: "memberships",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_tenants_slug",
                schema: "identity",
                table: "tenants",
                column: "slug",
                unique: true);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA identity FROM PUBLIC;
                GRANT USAGE ON SCHEMA identity TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON identity.tenants, identity.memberships, identity.user_profiles, identity.tenant_settings
                    TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA identity FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA identity FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security (ADR 0002, data-model §2.2–§2.3). Unset settings => NULL => no rows (fail-closed). ---
            migrationBuilder.Sql("""
                ALTER TABLE identity.tenants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.tenants FORCE ROW LEVEL SECURITY;
                ALTER TABLE identity.memberships ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.memberships FORCE ROW LEVEL SECURITY;
                ALTER TABLE identity.user_profiles ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.user_profiles FORCE ROW LEVEL SECURITY;
                ALTER TABLE identity.tenant_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.tenant_settings FORCE ROW LEVEL SECURITY;

                -- memberships: readable in the tenant context or for the caller's own rows (tenant resolution);
                -- writable only inside the tenant context.
                CREATE POLICY memberships_read ON identity.memberships FOR SELECT TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        OR user_id = NULLIF(current_setting('app.user_id', true), '')::uuid);
                CREATE POLICY memberships_write ON identity.memberships FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- tenants: readable in its own context or by its members; writable only in its own context.
                CREATE POLICY tenants_read ON identity.tenants FOR SELECT TO bayios_app
                    USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        OR EXISTS (SELECT 1 FROM identity.memberships m
                                   WHERE m.tenant_id = tenants.id
                                     AND m.user_id = NULLIF(current_setting('app.user_id', true), '')::uuid));
                CREATE POLICY tenants_write ON identity.tenants FOR ALL TO bayios_app
                    USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- user_profiles: own profile, or members of the current tenant; writable only by the user.
                CREATE POLICY user_profiles_read ON identity.user_profiles FOR SELECT TO bayios_app
                    USING (user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                        OR EXISTS (SELECT 1 FROM identity.memberships m
                                   WHERE m.user_id = user_profiles.user_id
                                     AND m.tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid));
                CREATE POLICY user_profiles_write ON identity.user_profiles FOR ALL TO bayios_app
                    USING (user_id = NULLIF(current_setting('app.user_id', true), '')::uuid)
                    WITH CHECK (user_id = NULLIF(current_setting('app.user_id', true), '')::uuid);

                -- tenant_settings: standard tenant isolation.
                CREATE POLICY tenant_isolation ON identity.tenant_settings FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memberships",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "tenant_settings",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_profiles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "identity");
        }
    }
}
