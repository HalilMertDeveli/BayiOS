using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BayiOS.Modules.Orders.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// #115 (KVKK, data-policy §5): customer anonymization masks the personal fields of the order address snapshots.
    /// <c>orders.anonymize_address_snapshot</c> is the single masking rule; the post-approval immutability trigger now
    /// lets exactly that change through, and only while the transaction-local marker <c>app.anonymize_customer_id</c>
    /// names the order's customer and <c>app.tenant_id</c> its tenant. Ids, the other columns and any other value
    /// stay rejected, for every role (the trigger fires for the table owner too).
    /// </summary>
    public partial class AnonymizeOrderAddressSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Masked: title, line1 (placeholder: the jsonb snapshot requires it), line2, postal code, contact name and
                -- phone. Kept: addressId (a removed customer address), district, city, country (coarse, non-identifying).
                -- Idempotent: masking a masked snapshot returns it unchanged. NULL stays NULL.
                CREATE FUNCTION orders.anonymize_address_snapshot(snapshot jsonb) RETURNS jsonb
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
                    SELECT snapshot || jsonb_build_object(
                        'title', NULL,
                        'line1', 'Silinmiş adres',
                        'line2', NULL,
                        'postalCode', NULL,
                        'contactName', NULL,
                        'contactPhone', NULL)
                $$;

                CREATE OR REPLACE FUNCTION orders.orders_address_snapshot_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status NOT IN ('draft', 'pending_approval')
                       AND (NEW.shipping_address IS DISTINCT FROM OLD.shipping_address
                            OR NEW.billing_address IS DISTINCT FROM OLD.billing_address
                            OR NEW.shipping_address_id IS DISTINCT FROM OLD.shipping_address_id
                            OR NEW.billing_address_id IS DISTINCT FROM OLD.billing_address_id) THEN
                        -- The one exception (#115): KVKK anonymization of this order's customer, in this transaction,
                        -- changing nothing but the snapshots, and each of them to exactly its masked form.
                        IF current_setting('app.anonymize_customer_id', true) = OLD.customer_id::text
                           AND current_setting('app.tenant_id', true) = OLD.tenant_id::text
                           AND NEW.customer_id = OLD.customer_id
                           AND NEW.shipping_address_id IS NOT DISTINCT FROM OLD.shipping_address_id
                           AND NEW.billing_address_id IS NOT DISTINCT FROM OLD.billing_address_id
                           AND NEW.shipping_address IS NOT DISTINCT FROM orders.anonymize_address_snapshot(OLD.shipping_address)
                           AND NEW.billing_address IS NOT DISTINCT FROM orders.anonymize_address_snapshot(OLD.billing_address) THEN
                            RETURN NEW;
                        END IF;

                        RAISE EXCEPTION 'order addresses cannot change after approval'
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION orders.orders_address_snapshot_immutable() RETURNS trigger
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

                DROP FUNCTION IF EXISTS orders.anonymize_address_snapshot(jsonb);
                """);
        }
    }
}
