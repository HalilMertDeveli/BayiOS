using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Billing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "tenant_entitlements",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_code = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    limit_value = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false, defaultValue: "override"),
                    reason = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_entitlements", x => x.id);
                    table.CheckConstraint("ck_tenant_entitlements_feature_code", "feature_code ~ '^[a-z][a-z0-9_]{1,62}$'");
                    table.CheckConstraint("ck_tenant_entitlements_limit_value", "limit_value IS NULL OR limit_value >= 0");
                    table.CheckConstraint("ck_tenant_entitlements_source", "source IN ('override')");
                });

            migrationBuilder.CreateTable(
                name: "tenant_modules",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_modules", x => x.id);
                    table.CheckConstraint("ck_tenant_modules_expiry", "status = 'active' OR expires_at IS NOT NULL");
                    table.CheckConstraint("ck_tenant_modules_module_code", "module_code IN ('ai_assistant', 'dealer_portal', 'serial_warranty', 'erp_integrations', 'whatsapp_automation', 'advanced_analytics')");
                    table.CheckConstraint("ck_tenant_modules_status", "status IN ('trialing', 'active', 'expired', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "tenant_subscriptions",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    trial_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trial_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    catalog_version = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: true),
                    provider_ref = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_subscriptions", x => x.id);
                    table.CheckConstraint("ck_tenant_subscriptions_period", "current_period_end IS NULL OR current_period_end > current_period_start");
                    table.CheckConstraint("ck_tenant_subscriptions_plan_code", "plan_code IN ('basic', 'professional')");
                    table.CheckConstraint("ck_tenant_subscriptions_status", "status IN ('trialing', 'active', 'expired', 'cancelled')");
                    table.CheckConstraint("ck_tenant_subscriptions_trial", "status <> 'trialing' OR (trial_started_at IS NOT NULL AND trial_ends_at IS NOT NULL AND trial_ends_at > trial_started_at)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_entitlements_tenant_id_feature_code",
                schema: "billing",
                table: "tenant_entitlements",
                columns: new[] { "tenant_id", "feature_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_modules_trialing_expires_at",
                schema: "billing",
                table: "tenant_modules",
                column: "expires_at",
                filter: "status = 'trialing'");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_modules_tenant_id_module_code",
                schema: "billing",
                table: "tenant_modules",
                columns: new[] { "tenant_id", "module_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_subscriptions_trialing_ends_at",
                schema: "billing",
                table: "tenant_subscriptions",
                column: "trial_ends_at",
                filter: "status = 'trialing'");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_subscriptions_tenant_id",
                schema: "billing",
                table: "tenant_subscriptions",
                column: "tenant_id",
                unique: true);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. Subscriptions and module
            // activations change state but are never deleted (history, PRD SUB-06); operator overrides (SUB-08) are
            // written by operators through the migrator role only — the app reads them. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA billing FROM PUBLIC;
                GRANT USAGE ON SCHEMA billing TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON billing.tenant_subscriptions TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON billing.tenant_modules TO bayios_app;
                GRANT SELECT ON billing.tenant_entitlements TO bayios_app;
                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA billing FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA billing FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['tenant_subscriptions', 'tenant_modules', 'tenant_entitlements'] LOOP
                        EXECUTE format('ALTER TABLE billing.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE billing.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON billing.%I FOR ALL TO bayios_app '
                            'USING (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid) '
                            'WITH CHECK (tenant_id = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid)', t);
                    END LOOP;
                END $$;
                """);

            // --- Trial expiry sweep (#51). A tenant-less job sees nothing under RLS, so it asks this SECURITY DEFINER
            // function (pinned search_path) which tenants have a trial whose end has passed. The owner's policies expose
            // only those rows (read-only) and the function returns tenant ids only; the job then updates each tenant in
            // its own tenant context, under the normal RLS policies. ---
            migrationBuilder.Sql("""
                CREATE POLICY tenant_subscriptions_expiry_select ON billing.tenant_subscriptions FOR SELECT TO bayios_migrator
                    USING (status = 'trialing' AND trial_ends_at <= now());
                CREATE POLICY tenant_modules_expiry_select ON billing.tenant_modules FOR SELECT TO bayios_migrator
                    USING (status = 'trialing' AND expires_at <= now());

                CREATE FUNCTION billing.due_subscription_tenants(p_limit integer) RETURNS SETOF uuid
                    LANGUAGE plpgsql
                    STABLE
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                BEGIN
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;

                    RETURN QUERY
                    SELECT due.tenant_id FROM (
                        SELECT s.tenant_id FROM billing.tenant_subscriptions s
                        WHERE s.status = 'trialing' AND s.trial_ends_at <= now()
                        UNION
                        SELECT m.tenant_id FROM billing.tenant_modules m
                        WHERE m.status = 'trialing' AND m.expires_at <= now()
                    ) due
                    ORDER BY due.tenant_id
                    LIMIT p_limit;
                END;
                $$;

                REVOKE ALL ON FUNCTION billing.due_subscription_tenants(integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION billing.due_subscription_tenants(integer) TO bayios_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS billing.due_subscription_tenants(integer);");

            migrationBuilder.DropTable(
                name: "tenant_entitlements",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "tenant_modules",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "tenant_subscriptions",
                schema: "billing");
        }
    }
}
