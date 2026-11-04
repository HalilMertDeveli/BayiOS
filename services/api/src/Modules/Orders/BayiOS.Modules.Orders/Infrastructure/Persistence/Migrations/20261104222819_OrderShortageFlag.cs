using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Orders.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderShortageFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_action",
                schema: "orders",
                table: "order_status_history");

            migrationBuilder.AddColumn<bool>(
                name: "has_shortage",
                schema: "orders",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_action",
                schema: "orders",
                table: "order_status_history",
                sql: "action IN ('create', 'submit', 'withdraw', 'reject', 'approve', 'start_picking', 'pack', 'ship', 'close_remaining', 'complete', 'cancel', 'report_shortage')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_order_status_history_action",
                schema: "orders",
                table: "order_status_history");

            migrationBuilder.DropColumn(
                name: "has_shortage",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_status_history_action",
                schema: "orders",
                table: "order_status_history",
                sql: "action IN ('create', 'submit', 'withdraw', 'reject', 'approve', 'start_picking', 'pack', 'ship', 'close_remaining', 'complete', 'cancel')");
        }
    }
}
