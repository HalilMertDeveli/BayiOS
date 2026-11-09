using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// In-transit stock of two-stage warehouse transfers (OD-01, #168): a balance row whose warehouse slot holds the
    /// transfer id, flagged <c>is_transit</c>. The ledger stays the single source of truth (dispatch = transfer_out at
    /// the source + transfer_in at the transit row; receipt = transfer_out at the transit row + transfer_in at the
    /// destination), so no unit is ever lost or counted twice, and transit rows belong to no warehouse (never
    /// available, never reservable). The CHECK keeps them non-negative, unreserved and location-less. No new grant:
    /// <c>bayios_app</c> already has SELECT/INSERT/UPDATE on the table; RLS (FORCE) is unchanged.
    /// </summary>
    public partial class TransitStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_transit",
                schema: "inventory",
                table: "inventory_balances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_inventory_balances_transit",
                schema: "inventory",
                table: "inventory_balances",
                sql: "NOT is_transit OR (on_hand >= 0 AND reserved = 0 AND location_id IS NULL AND NOT allow_negative)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inventory_balances_transit",
                schema: "inventory",
                table: "inventory_balances");

            migrationBuilder.DropColumn(
                name: "is_transit",
                schema: "inventory",
                table: "inventory_balances");
        }
    }
}
