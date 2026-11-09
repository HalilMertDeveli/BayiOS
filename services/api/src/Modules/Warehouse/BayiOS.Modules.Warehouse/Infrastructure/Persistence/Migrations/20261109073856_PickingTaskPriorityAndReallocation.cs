using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PickingTaskPriorityAndReallocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_picking_task_lines_quantities",
                schema: "warehouse",
                table: "picking_task_lines");

            migrationBuilder.AddColumn<string>(
                name: "priority",
                schema: "warehouse",
                table: "picking_tasks",
                type: "text",
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_picking_tasks_priority",
                schema: "warehouse",
                table: "picking_tasks",
                sql: "priority IN ('low', 'normal', 'high', 'urgent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_picking_task_lines_quantities",
                schema: "warehouse",
                table: "picking_task_lines",
                sql: "qty_to_pick >= 0 AND qty_picked >= 0 AND qty_short >= 0 AND qty_picked + qty_short <= qty_to_pick");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_picking_tasks_priority",
                schema: "warehouse",
                table: "picking_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_picking_task_lines_quantities",
                schema: "warehouse",
                table: "picking_task_lines");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "warehouse",
                table: "picking_tasks");

            migrationBuilder.AddCheckConstraint(
                name: "ck_picking_task_lines_quantities",
                schema: "warehouse",
                table: "picking_task_lines",
                sql: "qty_to_pick > 0 AND qty_picked >= 0 AND qty_short >= 0 AND qty_picked + qty_short <= qty_to_pick");
        }
    }
}
