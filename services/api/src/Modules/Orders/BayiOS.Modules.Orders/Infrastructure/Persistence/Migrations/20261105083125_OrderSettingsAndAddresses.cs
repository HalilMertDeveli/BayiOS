using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Orders.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderSettingsAndAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_order_number_sequences",
                schema: "orders",
                table: "order_number_sequences");

            migrationBuilder.AddColumn<string>(
                name: "billing_address",
                schema: "orders",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "billing_address_id",
                schema: "orders",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_address",
                schema: "orders",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shipping_address_id",
                schema: "orders",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prefix",
                schema: "orders",
                table: "order_number_sequences",
                type: "text",
                nullable: false,
                defaultValue: "SIP");

            migrationBuilder.AddPrimaryKey(
                name: "pk_order_number_sequences",
                schema: "orders",
                table: "order_number_sequences",
                columns: new[] { "tenant_id", "prefix", "year" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_number_sequences_prefix",
                schema: "orders",
                table: "order_number_sequences",
                sql: "prefix ~ '^[A-Z][A-Z0-9]{0,9}$'");

            // --- Address snapshots (#92) are frozen once the order is approved: the application writes them at submit
            // (draft → pending_approval) or at approval at the latest; afterwards no role may change them. ---
            migrationBuilder.Sql("""
                CREATE FUNCTION orders.orders_address_snapshot_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status NOT IN ('draft', 'pending_approval')
                       AND (NEW.shipping_address IS DISTINCT FROM OLD.shipping_address
                            OR NEW.billing_address IS DISTINCT FROM OLD.billing_address
                            OR NEW.shipping_address_id IS DISTINCT FROM OLD.shipping_address_id
                            OR NEW.billing_address_id IS DISTINCT FROM OLD.billing_address_id) THEN
                        RAISE EXCEPTION 'order addresses cannot change after approval'
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER trg_orders_address_snapshot_immutable
                    BEFORE UPDATE OF shipping_address, billing_address, shipping_address_id, billing_address_id ON orders.orders
                    FOR EACH ROW EXECUTE FUNCTION orders.orders_address_snapshot_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_orders_address_snapshot_immutable ON orders.orders;
                DROP FUNCTION IF EXISTS orders.orders_address_snapshot_immutable();
                """);

            migrationBuilder.DropPrimaryKey(
                name: "pk_order_number_sequences",
                schema: "orders",
                table: "order_number_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_number_sequences_prefix",
                schema: "orders",
                table: "order_number_sequences");

            migrationBuilder.DropColumn(
                name: "billing_address",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "billing_address_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_address",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_address_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "prefix",
                schema: "orders",
                table: "order_number_sequences");

            migrationBuilder.AddPrimaryKey(
                name: "pk_order_number_sequences",
                schema: "orders",
                table: "order_number_sequences",
                columns: new[] { "tenant_id", "year" });
        }
    }
}
