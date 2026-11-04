using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Imports.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "imports");

            migrationBuilder.CreateTable(
                name: "import_jobs",
                schema: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    file_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    file_format = table.Column<string>(type: "text", nullable: false),
                    file_encoding = table.Column<string>(type: "text", nullable: true),
                    delimiter = table.Column<string>(type: "text", nullable: true),
                    sheet_name = table.Column<string>(type: "text", nullable: true),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    sample_rows = table.Column<string>(type: "jsonb", nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    duplicate_of_import_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mapping = table.Column<string>(type: "jsonb", nullable: true),
                    match_key = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preview_create = table.Column<int>(type: "integer", nullable: false),
                    preview_update = table.Column<int>(type: "integer", nullable: false),
                    preview_skip = table.Column<int>(type: "integer", nullable: false),
                    preview_error = table.Column<int>(type: "integer", nullable: false),
                    processed_rows = table.Column<int>(type: "integer", nullable: false),
                    created_rows = table.Column<int>(type: "integer", nullable: false),
                    updated_rows = table.Column<int>(type: "integer", nullable: false),
                    skipped_rows = table.Column<int>(type: "integer", nullable: false),
                    failed_rows = table.Column<int>(type: "integer", nullable: false),
                    checkpoint_row = table.Column<int>(type: "integer", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                    table.UniqueConstraint("ak_import_jobs_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_import_jobs_counters", "total_rows >= 0 AND processed_rows >= 0 AND checkpoint_row >= 0");
                    table.CheckConstraint("ck_import_jobs_file_format", "file_format IN ('xlsx', 'csv')");
                    table.CheckConstraint("ck_import_jobs_file_sha256", "file_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_import_jobs_match_key", "match_key IN ('sku', 'barcode')");
                    table.CheckConstraint("ck_import_jobs_mode", "mode IN ('upsert', 'create_only', 'update_only')");
                    table.CheckConstraint("ck_import_jobs_status", "status IN ('uploaded', 'mapped', 'validating', 'validated', 'running', 'completed', 'failed', 'cancelled')");
                    table.CheckConstraint("ck_import_jobs_type", "type IN ('products', 'prices')");
                });

            migrationBuilder.CreateTable(
                name: "mapping_templates",
                schema: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    mapping = table.Column<string>(type: "jsonb", nullable: false),
                    match_key = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mapping_templates", x => x.id);
                    table.UniqueConstraint("ak_mapping_templates_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_mapping_templates_match_key", "match_key IN ('sku', 'barcode')");
                    table.CheckConstraint("ck_mapping_templates_mode", "mode IN ('upsert', 'create_only', 'update_only')");
                    table.CheckConstraint("ck_mapping_templates_type", "type IN ('products', 'prices')");
                });

            migrationBuilder.CreateTable(
                name: "import_files",
                schema: "imports",
                columns: table => new
                {
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_files", x => x.import_job_id);
                    table.ForeignKey(
                        name: "fk_import_files_import_jobs_tenant_id_import_job_id",
                        columns: x => new { x.tenant_id, x.import_job_id },
                        principalSchema: "imports",
                        principalTable: "import_jobs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_rows",
                schema: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    raw = table.Column<string>(type: "jsonb", nullable: false),
                    normalized = table.Column<string>(type: "jsonb", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    errors = table.Column<string>(type: "jsonb", nullable: false),
                    matched_variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_values = table.Column<string>(type: "jsonb", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_rows", x => x.id);
                    table.CheckConstraint("ck_import_rows_action", "action IN ('create', 'update', 'skip', 'error')");
                    table.CheckConstraint("ck_import_rows_row_number", "row_number >= 2");
                    table.CheckConstraint("ck_import_rows_status", "status IN ('valid', 'invalid', 'skipped', 'imported', 'failed')");
                    table.ForeignKey(
                        name: "fk_import_rows_import_jobs_tenant_id_import_job_id",
                        columns: x => new { x.tenant_id, x.import_job_id },
                        principalSchema: "imports",
                        principalTable: "import_jobs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_files_tenant_id_import_job_id",
                schema: "imports",
                table: "import_files",
                columns: new[] { "tenant_id", "import_job_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_tenant_id_created_at",
                schema: "imports",
                table: "import_jobs",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_tenant_id_file_sha256_type",
                schema: "imports",
                table: "import_jobs",
                columns: new[] { "tenant_id", "file_sha256", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_tenant_id_import_job_id_row_number",
                schema: "imports",
                table: "import_rows",
                columns: new[] { "tenant_id", "import_job_id", "row_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_tenant_id_import_job_id_status_row_number",
                schema: "imports",
                table: "import_rows",
                columns: new[] { "tenant_id", "import_job_id", "status", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ux_mapping_templates_tenant_id_type_name",
                schema: "imports",
                table: "mapping_templates",
                columns: new[] { "tenant_id", "type", "name" },
                unique: true);

            // --- Privileges (data-model §2.1). Runs as bayios_migrator, the schema/table owner.
            // Import jobs are history (never deleted by the app); staged rows are replaced on re-validation; raw files
            // are deleted after retention (data-policy: 30 days); templates are plain configuration. ---
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA imports FROM PUBLIC;
                GRANT USAGE ON SCHEMA imports TO bayios_app;
                GRANT SELECT, INSERT, UPDATE ON imports.import_jobs TO bayios_app;
                GRANT SELECT, INSERT, DELETE ON imports.import_files TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON imports.import_rows TO bayios_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON imports.mapping_templates TO bayios_app;

                -- Supabase client roles get nothing in business schemas (ADR 0002 §5). Absent locally.
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA imports FROM %I', r);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA imports FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Row level security: standard tenant isolation on every table (data-model §2.2). Fail-closed. ---
            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['import_jobs', 'import_files', 'import_rows', 'mapping_templates'] LOOP
                        EXECUTE format('ALTER TABLE imports.%I ENABLE ROW LEVEL SECURITY', t);
                        EXECUTE format('ALTER TABLE imports.%I FORCE ROW LEVEL SECURITY', t);
                        EXECUTE format(
                            'CREATE POLICY tenant_isolation ON imports.%I FOR ALL TO bayios_app '
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
                name: "import_files",
                schema: "imports");

            migrationBuilder.DropTable(
                name: "import_rows",
                schema: "imports");

            migrationBuilder.DropTable(
                name: "mapping_templates",
                schema: "imports");

            migrationBuilder.DropTable(
                name: "import_jobs",
                schema: "imports");
        }
    }
}
