using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Platform.Persistence.Migrations
{
    /// <summary>
    /// #89: the one reviewed exception to the append-only audit trail — retention purge of events older than the
    /// data-policy §4 period (2 years) — plus the index it walks. Everything else stays rejected for every role.
    /// </summary>
    public partial class AuditRetentionQueueCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                schema: "audit",
                table: "audit_events",
                column: "occurred_at");

            // --- The append-only trigger lets exactly one thing through: a DELETE of a row older than 2 years, run by
            // the table owner (bayios_migrator) while the transaction-local purge marker is set. Only
            // audit.purge_expired_audit_events (SECURITY DEFINER, owned by the migrator) sets the marker as the owner.
            // UPDATE and TRUNCATE are rejected unconditionally; bayios_app has no UPDATE/DELETE grant at all, so
            // setting the marker itself gets it nothing. The 2-year floor is hard-coded here: shortening retention
            // needs a reviewed migration, lengthening it is configuration (Audit:Retention).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit.reject_audit_change() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog
                AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND TG_LEVEL = 'ROW' THEN
                        IF current_setting('bayios.audit_retention_purge', true) = 'on'
                           AND current_user = (SELECT r.rolname FROM pg_catalog.pg_class c
                                               JOIN pg_catalog.pg_roles r ON r.oid = c.relowner
                                               WHERE c.oid = TG_RELID)
                           AND OLD.occurred_at < now() - interval '2 years' THEN
                            RETURN OLD;
                        END IF;
                    END IF;

                    RAISE EXCEPTION 'audit.audit_events is append-only'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                """);

            // --- The owner is under FORCE RLS too: it may see and delete only rows past the floor (same pattern as
            // the idempotency purge policies).
            migrationBuilder.Sql("""
                CREATE POLICY audit_retention_select ON audit.audit_events FOR SELECT TO bayios_migrator
                    USING (occurred_at < now() - interval '2 years');
                CREATE POLICY audit_retention_delete ON audit.audit_events FOR DELETE TO bayios_migrator
                    USING (occurred_at < now() - interval '2 years');

                CREATE FUNCTION audit.purge_expired_audit_events(p_retention interval, p_limit integer) RETURNS integer
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    cutoff timestamptz;
                    deleted integer;
                BEGIN
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;
                    IF p_retention IS NULL OR p_retention < interval '730 days' THEN
                        RAISE EXCEPTION 'audit retention cannot be shorter than 730 days (data-policy section 4)';
                    END IF;

                    cutoff := least(now() - p_retention, now() - interval '2 years');

                    PERFORM set_config('bayios.audit_retention_purge', 'on', true);
                    DELETE FROM audit.audit_events
                    WHERE occurred_at < cutoff
                      AND id IN (SELECT id FROM audit.audit_events
                                 WHERE occurred_at < cutoff
                                 ORDER BY occurred_at
                                 LIMIT p_limit);
                    GET DIAGNOSTICS deleted = ROW_COUNT;
                    PERFORM set_config('bayios.audit_retention_purge', '', true);
                    RETURN deleted;
                END;
                $$;

                REVOKE ALL ON FUNCTION audit.purge_expired_audit_events(interval, integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION audit.purge_expired_audit_events(interval, integer) TO bayios_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS audit.purge_expired_audit_events(interval, integer);
                DROP POLICY IF EXISTS audit_retention_select ON audit.audit_events;
                DROP POLICY IF EXISTS audit_retention_delete ON audit.audit_events;

                CREATE OR REPLACE FUNCTION audit.reject_audit_change() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog
                AS $$
                BEGIN
                    RAISE EXCEPTION 'audit.audit_events is append-only'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_audit_events_occurred_at",
                schema: "audit",
                table: "audit_events");
        }
    }
}
