using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditJobsOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_role = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    ip_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_action", "action ~ '^[a-z][a-z0-9_]*([.][a-z][a-z0-9_]*)+$' AND char_length(action) <= 100");
                    table.CheckConstraint("ck_audit_events_changes", "changes IS NULL OR pg_column_size(changes) <= 32768");
                    table.CheckConstraint("ck_audit_events_entity_type", "char_length(entity_type) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_audit_events_user_agent", "user_agent IS NULL OR char_length(user_agent) <= 200");
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_by = table.Column<string>(type: "text", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempts", "attempts >= 0 AND max_attempts BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued', 'running', 'succeeded', 'dead')");
                    table.CheckConstraint("ck_jobs_type", "char_length(type) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_type", "char_length(type) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_entity",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "occurred_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_occurred",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "tenant_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_finished",
                schema: "platform",
                table: "jobs",
                column: "finished_at",
                filter: "status IN ('succeeded', 'dead')");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_queued",
                schema: "platform",
                table: "jobs",
                columns: new[] { "priority", "run_after" },
                descending: new[] { true, false },
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_running_lease",
                schema: "platform",
                table: "jobs",
                column: "locked_until",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_type_dedupe_key",
                schema: "platform",
                table: "jobs",
                columns: new[] { "type", "dedupe_key" },
                unique: true,
                filter: "dedupe_key IS NOT NULL AND status IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "platform",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed",
                schema: "platform",
                table: "outbox_messages",
                column: "processed_at",
                filter: "processed_at IS NOT NULL");

            // --- Audit is append-only (threat-model §7), like the stock ledger: no UPDATE/DELETE grant for the app
            // and a trigger that also stops the owner. Retention (data-policy §6) will need an explicit, reviewed path.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.reject_audit_change() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog
                AS $$
                BEGIN
                    RAISE EXCEPTION 'audit.audit_events is append-only'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER trg_audit_events_append_only
                    BEFORE UPDATE OR DELETE ON audit.audit_events
                    FOR EACH ROW EXECUTE FUNCTION audit.reject_audit_change();
                CREATE TRIGGER trg_audit_events_no_truncate
                    BEFORE TRUNCATE ON audit.audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_audit_change();
                """);

            // --- Jobs and outbox have no RLS (data-model §2.3, accepted Phase 0 decision): the multi-tenant worker
            // must see every tenant's queue. Guard against forged tenants instead: inside a tenant context (a request,
            // or a job handler) a row may only be written for that same tenant. Without a tenant context (the worker's
            // own claim/dispatch transactions) any tenant id is accepted.
            migrationBuilder.Sql("""
                CREATE FUNCTION platform.enforce_queue_tenant() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    current_tenant text := coalesce(current_setting('app.tenant_id', true), '');
                BEGIN
                    IF current_tenant <> '' AND NEW.tenant_id IS DISTINCT FROM current_tenant::uuid THEN
                        RAISE EXCEPTION '% row tenant does not match app.tenant_id', TG_TABLE_NAME
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_jobs_tenant
                    BEFORE INSERT OR UPDATE OF tenant_id ON platform.jobs
                    FOR EACH ROW EXECUTE FUNCTION platform.enforce_queue_tenant();
                CREATE TRIGGER trg_outbox_messages_tenant
                    BEFORE INSERT OR UPDATE OF tenant_id ON platform.outbox_messages
                    FOR EACH ROW EXECUTE FUNCTION platform.enforce_queue_tenant();
                """);

            // --- Expired idempotency keys are purged by a platform job (ADR 0006 §5) that has no tenant context.
            // Instead of loosening idempotency_scope for bayios_app (rows hold stored responses), a SECURITY DEFINER
            // function runs as the owner, and only the owner may see and delete rows that have already expired.
            migrationBuilder.Sql("""
                CREATE POLICY idempotency_purge_select ON platform.idempotency_keys FOR SELECT TO bayios_migrator
                    USING (expires_at < now());
                CREATE POLICY idempotency_purge_delete ON platform.idempotency_keys FOR DELETE TO bayios_migrator
                    USING (expires_at < now());

                CREATE FUNCTION platform.purge_expired_idempotency_keys(p_limit integer) RETURNS integer
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    deleted integer;
                BEGIN
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;

                    DELETE FROM platform.idempotency_keys
                    WHERE expires_at < now()
                      AND id IN (SELECT id FROM platform.idempotency_keys
                                 WHERE expires_at < now()
                                 ORDER BY expires_at
                                 LIMIT p_limit);
                    GET DIAGNOSTICS deleted = ROW_COUNT;
                    RETURN deleted;
                END;
                $$;
                """);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA audit FROM PUBLIC;
                REVOKE ALL ON FUNCTION audit.reject_audit_change() FROM PUBLIC;
                REVOKE ALL ON FUNCTION platform.enforce_queue_tenant() FROM PUBLIC;
                REVOKE ALL ON FUNCTION platform.purge_expired_idempotency_keys(integer) FROM PUBLIC;
                GRANT USAGE ON SCHEMA audit TO bayios_app;
                GRANT SELECT, INSERT ON audit.audit_events TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON platform.jobs, platform.outbox_messages TO bayios_app;
                GRANT EXECUTE ON FUNCTION platform.purge_expired_idempotency_keys(integer) TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA audit FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA audit FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA platform FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security (data-model §2.2 standard tenant pattern) for audit reads and writes. ---
            migrationBuilder.Sql("""
                ALTER TABLE audit.audit_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit.audit_events FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON audit.audit_events FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS platform.purge_expired_idempotency_keys(integer);
                DROP POLICY IF EXISTS idempotency_purge_select ON platform.idempotency_keys;
                DROP POLICY IF EXISTS idempotency_purge_delete ON platform.idempotency_keys;
                """);

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "platform");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS audit.reject_audit_change();
                DROP FUNCTION IF EXISTS platform.enforce_queue_tenant();
                """);
        }
    }
}
