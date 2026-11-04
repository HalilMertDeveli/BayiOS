using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "logo_object_path",
                schema: "identity",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                schema: "identity",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_number",
                schema: "identity",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_office",
                schema: "identity",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "default_tax_rate",
                schema: "identity",
                table: "tenant_settings",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 20m);

            migrationBuilder.AddColumn<bool>(
                name: "prices_include_tax_display",
                schema: "identity",
                table: "tenant_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_default_currency",
                schema: "identity",
                table: "tenants",
                sql: "default_currency ~ '^[A-Z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_settings_default_tax_rate",
                schema: "identity",
                table: "tenant_settings",
                sql: "default_tax_rate BETWEEN 0 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_default_currency",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_settings_default_tax_rate",
                schema: "identity",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "logo_object_path",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "phone",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "tax_number",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "tax_office",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "default_tax_rate",
                schema: "identity",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "prices_include_tax_display",
                schema: "identity",
                table: "tenant_settings");
        }
    }
}
