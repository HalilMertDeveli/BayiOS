using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "order_number_prefix",
                schema: "identity",
                table: "tenant_settings",
                type: "text",
                nullable: false,
                defaultValue: "SIP");

            migrationBuilder.AddColumn<bool>(
                name: "require_order_approval",
                schema: "identity",
                table: "tenant_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_settings_order_number_prefix",
                schema: "identity",
                table: "tenant_settings",
                sql: "order_number_prefix ~ '^[A-Z][A-Z0-9]{0,9}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_settings_order_number_prefix",
                schema: "identity",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "order_number_prefix",
                schema: "identity",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "require_order_approval",
                schema: "identity",
                table: "tenant_settings");
        }
    }
}
