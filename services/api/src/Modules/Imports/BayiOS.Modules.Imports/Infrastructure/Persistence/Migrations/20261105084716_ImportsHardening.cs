using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Imports.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportsHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_import_jobs_status",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.AddColumn<string>(
                name: "applied_values",
                schema: "imports",
                table: "import_rows",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rollback_status",
                schema: "imports",
                table: "import_rows",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rolled_back_at",
                schema: "imports",
                table: "import_rows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rollback_conflict_rows",
                schema: "imports",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "rollback_failed_rows",
                schema: "imports",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rollback_finished_at",
                schema: "imports",
                table: "import_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rollback_requested_at",
                schema: "imports",
                table: "import_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rollback_requested_by",
                schema: "imports",
                table: "import_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rollback_skipped_rows",
                schema: "imports",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "rolled_back_rows",
                schema: "imports",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_rows_rollback_status",
                schema: "imports",
                table: "import_rows",
                sql: "rollback_status IS NULL OR rollback_status IN ('rolled_back', 'conflict', 'failed', 'skipped')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_jobs_rollback_counters",
                schema: "imports",
                table: "import_jobs",
                sql: "rolled_back_rows >= 0 AND rollback_conflict_rows >= 0 AND rollback_failed_rows >= 0 AND rollback_skipped_rows >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_jobs_status",
                schema: "imports",
                table: "import_jobs",
                sql: "status IN ('uploaded', 'mapped', 'validating', 'validated', 'running', 'completed', 'failed', 'cancelled', 'rolling_back', 'rolled_back', 'partially_rolled_back', 'rollback_failed')");

            // --- Raw file retention (data-policy §4: import files 30 days after the import finished). The purge runs
            // in a tenant-less maintenance job, which sees nothing under RLS. Instead of loosening tenant_isolation for
            // bayios_app, a SECURITY DEFINER function runs as the owner, and the owner may only see/delete files (and
            // read their jobs) past a 1-day floor of inactivity — same pattern as platform.purge_expired_idempotency_keys.
            // Job metadata and row results stay; rollback never needs the file. ---
            migrationBuilder.Sql("""
                CREATE POLICY import_files_purge_select ON imports.import_files FOR SELECT TO bayios_migrator
                    USING (created_at < now() - interval '1 day');
                CREATE POLICY import_files_purge_delete ON imports.import_files FOR DELETE TO bayios_migrator
                    USING (created_at < now() - interval '1 day');
                CREATE POLICY import_jobs_purge_select ON imports.import_jobs FOR SELECT TO bayios_migrator
                    USING (status NOT IN ('validating', 'running', 'rolling_back')
                           AND coalesce(finished_at, created_at) < now() - interval '1 day');

                CREATE FUNCTION imports.purge_expired_files(p_retention interval, p_limit integer) RETURNS integer
                    LANGUAGE plpgsql
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    purged integer;
                BEGIN
                    IF p_limit IS NULL OR p_limit < 1 OR p_limit > 10000 THEN
                        RAISE EXCEPTION 'p_limit must be between 1 and 10000';
                    END IF;
                    IF p_retention IS NULL OR p_retention < interval '1 day' THEN
                        RAISE EXCEPTION 'p_retention must be at least 1 day';
                    END IF;

                    DELETE FROM imports.import_files f
                    USING (
                        SELECT x.tenant_id, x.import_job_id
                        FROM imports.import_files x
                        JOIN imports.import_jobs j ON j.tenant_id = x.tenant_id AND j.id = x.import_job_id
                        WHERE j.status NOT IN ('validating', 'running', 'rolling_back')
                          AND coalesce(j.finished_at, j.created_at) < now() - p_retention
                        ORDER BY coalesce(j.finished_at, j.created_at)
                        LIMIT p_limit
                    ) e
                    WHERE f.tenant_id = e.tenant_id AND f.import_job_id = e.import_job_id;
                    GET DIAGNOSTICS purged = ROW_COUNT;
                    RETURN purged;
                END;
                $$;

                REVOKE ALL ON FUNCTION imports.purge_expired_files(interval, integer) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION imports.purge_expired_files(interval, integer) TO bayios_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS imports.purge_expired_files(interval, integer);
                DROP POLICY IF EXISTS import_jobs_purge_select ON imports.import_jobs;
                DROP POLICY IF EXISTS import_files_purge_delete ON imports.import_files;
                DROP POLICY IF EXISTS import_files_purge_select ON imports.import_files;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_import_rows_rollback_status",
                schema: "imports",
                table: "import_rows");

            migrationBuilder.DropCheckConstraint(
                name: "ck_import_jobs_rollback_counters",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_import_jobs_status",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "applied_values",
                schema: "imports",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "rollback_status",
                schema: "imports",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "rolled_back_at",
                schema: "imports",
                table: "import_rows");

            migrationBuilder.DropColumn(
                name: "rollback_conflict_rows",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "rollback_failed_rows",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "rollback_finished_at",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "rollback_requested_at",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "rollback_requested_by",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "rollback_skipped_rows",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "rolled_back_rows",
                schema: "imports",
                table: "import_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_import_jobs_status",
                schema: "imports",
                table: "import_jobs",
                sql: "status IN ('uploaded', 'mapped', 'validating', 'validated', 'running', 'completed', 'failed', 'cancelled')");
        }
    }
}
