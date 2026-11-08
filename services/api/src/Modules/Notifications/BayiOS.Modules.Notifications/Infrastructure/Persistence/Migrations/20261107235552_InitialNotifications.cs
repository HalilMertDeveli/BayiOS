using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "device_tokens",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    token_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    app_version = table.Column<string>(type: "text", nullable: true),
                    locale = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_tokens", x => x.id);
                    table.UniqueConstraint("ak_device_tokens_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_device_tokens_app_version", "app_version IS NULL OR length(app_version) BETWEEN 1 AND 32");
                    table.CheckConstraint("ck_device_tokens_locale", "locale IS NULL OR length(locale) BETWEEN 2 AND 35");
                    table.CheckConstraint("ck_device_tokens_platform", "platform IN ('android', 'ios')");
                    table.CheckConstraint("ck_device_tokens_seen", "last_seen_at >= created_at");
                    table.CheckConstraint("ck_device_tokens_token", "length(token) BETWEEN 32 AND 4096 AND token ~ '^[A-Za-z0-9_:.-]+$'");
                    table.CheckConstraint("ck_device_tokens_token_sha256", "length(token_sha256) = 32");
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_tokens_last_seen_at",
                schema: "notifications",
                table: "device_tokens",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_tokens_tenant_id_user_id_last_seen_at",
                schema: "notifications",
                table: "device_tokens",
                columns: new[] { "tenant_id", "user_id", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ux_device_tokens_token_sha256",
                schema: "notifications",
                table: "device_tokens",
                column: "token_sha256",
                unique: true);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. A registration is
            // deleted on sign-out, on FCM "unregistered" and by the stale sweep, so the runtime role may DELETE. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA notifications FROM PUBLIC;
                GRANT USAGE ON SCHEMA notifications TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON notifications.device_tokens TO bayios_app;
                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA notifications FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA notifications FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security (data-model §2.2/§2.3). Tenant isolation plus the user: a request (app.user_id set)
            // sees and writes only its own user's registrations; a job (tenant, no user) sees the tenant's, to send to
            // the assignee. Fail-closed: no tenant → nothing. ---
            migrationBuilder.Sql("""
                ALTER TABLE notifications.device_tokens ENABLE ROW LEVEL SECURITY;
                ALTER TABLE notifications.device_tokens FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON notifications.device_tokens FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                           AND (NULLIF(current_setting('app.user_id', true), '') IS NULL
                                OR user_id = NULLIF(current_setting('app.user_id', true), '')::uuid))
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                           AND (NULLIF(current_setting('app.user_id', true), '') IS NULL
                                OR user_id = NULLIF(current_setting('app.user_id', true), '')::uuid));
                """);

            // --- Token hand-over (#44). A token is unique across tenants (ux_device_tokens_token_sha256): when another
            // user — or the same user in another tenant — registers it, the device changed hands and the old row must
            // go, but the runtime role cannot see it under RLS. This SECURITY DEFINER function (pinned search_path,
            // owner bayios_migrator) deletes exactly the rows holding that token hash for a different (tenant, user)
            // than the caller's request context, under a per-token advisory lock held until commit (two concurrent
            // registrations never both keep it). The owner's FORCE RLS exposes only the rows of the hash named in the
            // transaction-local bayios.device_token_handover, which only this function sets. Knowing the token (the
            // device's own secret) is what authorises the move; nothing is returned but a count. ---
            migrationBuilder.Sql("""
                CREATE POLICY device_tokens_handover_select ON notifications.device_tokens FOR SELECT TO bayios_migrator
                    USING (token_sha256 = decode(NULLIF(current_setting('bayios.device_token_handover', true), ''), 'hex'));
                CREATE POLICY device_tokens_handover_delete ON notifications.device_tokens FOR DELETE TO bayios_migrator
                    USING (token_sha256 = decode(NULLIF(current_setting('bayios.device_token_handover', true), ''), 'hex'));

                CREATE FUNCTION notifications.release_device_token(p_token_sha256 bytea) RETURNS integer
                    LANGUAGE plpgsql
                    VOLATILE
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    v_tenant uuid := NULLIF(current_setting('app.tenant_id', true), '')::uuid;
                    v_user uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                    v_deleted integer;
                BEGIN
                    IF v_tenant IS NULL OR v_user IS NULL THEN
                        RAISE EXCEPTION 'release_device_token needs a tenant and a user context';
                    END IF;
                    IF p_token_sha256 IS NULL OR length(p_token_sha256) <> 32 THEN
                        RAISE EXCEPTION 'p_token_sha256 must be a SHA-256 hash';
                    END IF;

                    PERFORM pg_advisory_xact_lock(hashtextextended('notifications.device_token:' || encode(p_token_sha256, 'hex'), 0));
                    PERFORM set_config('bayios.device_token_handover', encode(p_token_sha256, 'hex'), true);
                    DELETE FROM notifications.device_tokens d
                    WHERE d.token_sha256 = p_token_sha256
                      AND (d.tenant_id <> v_tenant OR d.user_id <> v_user);
                    GET DIAGNOSTICS v_deleted = ROW_COUNT;
                    PERFORM set_config('bayios.device_token_handover', '', true);
                    RETURN v_deleted;
                END;
                $$;

                REVOKE ALL ON FUNCTION notifications.release_device_token(bytea) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION notifications.release_device_token(bytea) TO bayios_app;
                """);

            // --- Stale sweep (#44). A tenant-less maintenance job sees nothing under RLS; this SECURITY DEFINER function
            // deletes registrations not refreshed for p_max_age (never less than the 30-day floor), oldest first. The
            // owner's purge policies expose only rows past that floor. ---
            migrationBuilder.Sql("""
                CREATE POLICY device_tokens_purge_select ON notifications.device_tokens FOR SELECT TO bayios_migrator
                    USING (last_seen_at < now() - interval '30 days');
                CREATE POLICY device_tokens_purge_delete ON notifications.device_tokens FOR DELETE TO bayios_migrator
                    USING (last_seen_at < now() - interval '30 days');

                CREATE FUNCTION notifications.purge_stale_device_tokens(p_max_age interval, p_limit integer) RETURNS integer
                    LANGUAGE plpgsql
                    VOLATILE
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    v_deleted integer;
                BEGIN
                    IF p_max_age IS NULL OR p_max_age < interval '30 days' THEN
                        RAISE EXCEPTION 'p_max_age must be at least 30 days';
                    END IF;
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;

                    DELETE FROM notifications.device_tokens
                    WHERE id IN (
                        SELECT d.id FROM notifications.device_tokens d
                        WHERE d.last_seen_at < now() - greatest(p_max_age, interval '30 days')
                        ORDER BY d.last_seen_at, d.id
                        LIMIT p_limit);
                    GET DIAGNOSTICS v_deleted = ROW_COUNT;
                    RETURN v_deleted;
                END;
                $$;

                REVOKE ALL ON FUNCTION notifications.purge_stale_device_tokens(interval, integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION notifications.purge_stale_device_tokens(interval, integer) TO bayios_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notifications.purge_stale_device_tokens(interval, integer);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notifications.release_device_token(bytea);");

            migrationBuilder.DropTable(
                name: "device_tokens",
                schema: "notifications");
        }
    }
}
