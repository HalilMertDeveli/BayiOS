using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemberInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                    table.CheckConstraint("ck_invitations_accepted", "(status = 'accepted') = (accepted_at IS NOT NULL AND accepted_by IS NOT NULL)");
                    table.CheckConstraint("ck_invitations_email", "email = lower(btrim(email)) AND length(email) BETWEEN 3 AND 254 AND position('@' in email) > 1");
                    table.CheckConstraint("ck_invitations_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_invitations_revoked", "(status = 'revoked') = (revoked_at IS NOT NULL AND revoked_by IS NOT NULL)");
                    table.CheckConstraint("ck_invitations_role", "role IN ('admin', 'manager', 'sales', 'warehouse_staff', 'viewer')");
                    table.CheckConstraint("ck_invitations_status", "status IN ('pending', 'accepted', 'revoked', 'expired')");
                    table.CheckConstraint("ck_invitations_token_count", "token_count >= 1");
                    table.CheckConstraint("ck_invitations_token_hash", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_invitations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_tenant_status_expires",
                schema: "identity",
                table: "invitations",
                columns: new[] { "tenant_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_invitations_pending_email",
                schema: "identity",
                table: "invitations",
                columns: new[] { "tenant_id", "email" },
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ux_invitations_token_hash",
                schema: "identity",
                table: "invitations",
                column: "token_hash",
                unique: true);

            // --- Privileges: no DELETE (invitations are revoked/accepted/expired, never removed by the app). ---
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON identity.invitations TO bayios_app;
                DO $$
                DECLARE r text;
                BEGIN
                    FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                            EXECUTE format('REVOKE ALL ON identity.invitations FROM %I', r);
                        END IF;
                    END LOOP;
                END $$;
                """);

            // --- Standard tenant RLS (data-model §2.2), fail-closed. ---
            migrationBuilder.Sql("""
                ALTER TABLE identity.invitations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE identity.invitations FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON identity.invitations FOR ALL TO bayios_app
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // --- Acceptance path (phase0-gate F-25 / C-5). The invitee is no member yet, so RLS hides the invitation; this
            // SECURITY DEFINER function (pinned search_path, owner = bayios_migrator) maps the SHA-256 of a token to its
            // tenant id and nothing else. The owner's policy exposes pending rows only, read-only. The API then switches
            // to that tenant's context and does everything else under the normal RLS policy. ---
            migrationBuilder.Sql("""
                CREATE POLICY invitations_token_lookup ON identity.invitations FOR SELECT TO bayios_migrator
                    USING (status = 'pending');

                CREATE FUNCTION identity.find_invitation_tenant(p_token_hash bytea) RETURNS uuid
                    LANGUAGE sql
                    STABLE
                    SECURITY DEFINER
                    SET search_path = pg_catalog
                AS $$
                    SELECT i.tenant_id FROM identity.invitations i
                    WHERE p_token_hash IS NOT NULL
                      AND octet_length(p_token_hash) = 32
                      AND i.token_hash = p_token_hash
                      AND i.status = 'pending'
                $$;

                REVOKE ALL ON FUNCTION identity.find_invitation_tenant(bytea) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION identity.find_invitation_tenant(bytea) TO bayios_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.find_invitation_tenant(bytea);");

            migrationBuilder.DropTable(
                name: "invitations",
                schema: "identity");
        }
    }
}
