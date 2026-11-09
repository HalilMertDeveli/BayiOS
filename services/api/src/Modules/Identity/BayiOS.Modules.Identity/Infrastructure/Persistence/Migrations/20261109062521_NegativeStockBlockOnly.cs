using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Owner decision OD-09 (2026-11-09): stock never goes below zero. Every tenant's <c>negative_stock_policy</c>
    /// becomes <c>block</c> and the CHECK accepts nothing else. FORCE RLS also binds the owner (bayios_migrator), who
    /// has no policy on this table, so the data step lifts FORCE for the one UPDATE inside the migration transaction
    /// and restores it before the constraint is added (the constraint validates every row regardless of RLS).
    /// </summary>
    public partial class NegativeStockBlockOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_settings_negative_stock_policy",
                schema: "identity",
                table: "tenant_settings");

            migrationBuilder.Sql("""
                ALTER TABLE identity.tenant_settings NO FORCE ROW LEVEL SECURITY;
                UPDATE identity.tenant_settings
                   SET negative_stock_policy = 'block', row_version = row_version + 1, updated_at = now()
                 WHERE negative_stock_policy <> 'block';
                ALTER TABLE identity.tenant_settings FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_settings_negative_stock_policy",
                schema: "identity",
                table: "tenant_settings",
                sql: "negative_stock_policy = 'block'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Widens the CHECK only; converted rows stay 'block' (the former value is not kept).
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_settings_negative_stock_policy",
                schema: "identity",
                table: "tenant_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_settings_negative_stock_policy",
                schema: "identity",
                table: "tenant_settings",
                sql: "negative_stock_policy IN ('block', 'warn', 'allow')");
        }
    }
}
