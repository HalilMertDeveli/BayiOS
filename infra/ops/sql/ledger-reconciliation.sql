-- BayiOS ledger reconciliation (#55, runbooks/stock-inconsistency.md). READ ONLY.
-- inventory_movements is the single source of truth (ADR 0004); inventory_balances is derived and updated in the same
-- transaction. Every row returned is an inconsistency:
--   kind = on_hand   balance.on_hand  <> sum(movements.quantity)          for (tenant, variant, warehouse, location)
--   kind = reserved  balance.reserved <> sum(open quantity of active reservations) for the same key
-- An empty result means the derived balances match the ledger.
--
-- Operator, across all tenants (admin / BYPASSRLS session, e.g. Supabase SQL editor in a read-only transaction):
--   psql "<admin url>" -v ON_ERROR_STOP=1 -c 'BEGIN READ ONLY' -f infra/ops/sql/ledger-reconciliation.sql
-- One tenant only, as bayios_app (RLS limits it to that tenant):
--   BEGIN READ ONLY; SELECT set_config('app.tenant_id', '<tenant uuid>', true); \i infra/ops/sql/ledger-reconciliation.sql
-- Output holds ids and quantities only — no names, prices or personal data.
WITH ledger AS (
    SELECT tenant_id, variant_id, warehouse_id, location_id, sum(quantity) AS quantity
    FROM inventory.inventory_movements
    GROUP BY tenant_id, variant_id, warehouse_id, location_id
),
reservations AS (
    SELECT tenant_id, variant_id, warehouse_id, location_id, sum(quantity - consumed_quantity - released_quantity) AS quantity
    FROM inventory.stock_reservations
    WHERE status = 'active'
    GROUP BY tenant_id, variant_id, warehouse_id, location_id
),
on_hand AS (
    SELECT 'on_hand'::text AS kind,
           coalesce(b.tenant_id, l.tenant_id) AS tenant_id,
           coalesce(b.variant_id, l.variant_id) AS variant_id,
           coalesce(b.warehouse_id, l.warehouse_id) AS warehouse_id,
           coalesce(b.location_id, l.location_id) AS location_id,
           b.on_hand AS balance,
           coalesce(l.quantity, 0) AS expected
    FROM inventory.inventory_balances b
    FULL JOIN ledger l
      ON l.tenant_id = b.tenant_id AND l.variant_id = b.variant_id AND l.warehouse_id = b.warehouse_id
     AND l.location_id IS NOT DISTINCT FROM b.location_id
    WHERE coalesce(b.on_hand, 0) <> coalesce(l.quantity, 0)
),
reserved AS (
    SELECT 'reserved'::text AS kind,
           coalesce(b.tenant_id, r.tenant_id) AS tenant_id,
           coalesce(b.variant_id, r.variant_id) AS variant_id,
           coalesce(b.warehouse_id, r.warehouse_id) AS warehouse_id,
           coalesce(b.location_id, r.location_id) AS location_id,
           b.reserved AS balance,
           coalesce(r.quantity, 0) AS expected
    FROM inventory.inventory_balances b
    FULL JOIN reservations r
      ON r.tenant_id = b.tenant_id AND r.variant_id = b.variant_id AND r.warehouse_id = b.warehouse_id
     AND r.location_id IS NOT DISTINCT FROM b.location_id
    WHERE coalesce(b.reserved, 0) <> coalesce(r.quantity, 0)
)
SELECT kind, tenant_id, variant_id, warehouse_id, location_id, balance, expected, coalesce(balance, 0) - expected AS difference
FROM (SELECT * FROM on_hand UNION ALL SELECT * FROM reserved) inconsistencies
ORDER BY tenant_id, kind, variant_id, warehouse_id, location_id;
