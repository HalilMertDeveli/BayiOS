-- Stock invariants after a load / soak run (#58). Read-only; run as the container superuser (sees every tenant).
-- Fails (ON_ERROR_STOP) when any invariant is broken:
--   I1 every movement has a known type and the sign of its direction (in > 0, out < 0)
--   I2 balance.on_hand = signed sum of the ledger (inventory_movements) for the same tenant/variant/warehouse/location
--   I3 balance.reserved = open reservation quantity (quantity - consumed - released) for the same key
--   I4 no oversell: on_hand >= 0 and reserved <= on_hand wherever negative stock is not allowed
--   I5 no reservation has a negative open quantity, and closed (consumed/released) reservations have none left
\set QUIET on
\pset footer off

CREATE TEMP TABLE ledger AS
SELECT tenant_id, variant_id, warehouse_id, coalesce(location_id, '00000000-0000-0000-0000-000000000000') AS loc,
       sum(quantity) AS on_hand, -- the ledger stores signed quantities (out < 0); I1 checks the signs
       count(*) AS movements
FROM inventory.inventory_movements
GROUP BY 1, 2, 3, 4;

CREATE TEMP TABLE open_reservations AS
SELECT tenant_id, variant_id, warehouse_id, coalesce(location_id, '00000000-0000-0000-0000-000000000000') AS loc, sum(quantity - consumed_quantity - released_quantity) AS reserved
FROM inventory.stock_reservations
GROUP BY 1, 2, 3, 4;

CREATE TEMP TABLE balances AS
SELECT tenant_id, variant_id, warehouse_id, coalesce(location_id, '00000000-0000-0000-0000-000000000000') AS loc, on_hand, reserved
FROM inventory.inventory_balances;

\echo '-- totals'
SELECT (SELECT count(*) FROM inventory.inventory_movements) AS movements,
       (SELECT count(*) FROM inventory.inventory_balances) AS balances,
       (SELECT count(*) FROM inventory.stock_reservations) AS reservations,
       (SELECT count(*) FROM inventory.stock_reservations WHERE status = 'active') AS active_reservations,
       (SELECT sum(on_hand) FROM inventory.inventory_balances) AS on_hand_total,
       (SELECT sum(reserved) FROM inventory.inventory_balances) AS reserved_total;
\echo '-- orders by status'
SELECT status, count(*) FROM orders.orders GROUP BY status ORDER BY status;

DO $$
DECLARE
  unknown_types bigint;
  ledger_mismatch bigint;
  reserved_mismatch bigint;
  oversold bigint;
  bad_reservations bigint;
BEGIN
  SELECT count(*) INTO unknown_types FROM inventory.inventory_movements
  WHERE NOT ((type IN ('receipt', 'adjustment_in', 'transfer_in', 'return_in') AND quantity > 0)
          OR (type IN ('adjustment_out', 'transfer_out', 'shipment_out', 'damage_out') AND quantity < 0)
          OR (type = 'count_correction' AND quantity <> 0));

  -- NULL location = warehouse without shelves; coalesced so the FULL JOIN pairs NULL with NULL.
  SELECT count(*) INTO ledger_mismatch
  FROM balances b
  FULL JOIN ledger l USING (tenant_id, variant_id, warehouse_id, loc)
  WHERE coalesce(b.on_hand, 0) <> coalesce(l.on_hand, 0);

  SELECT count(*) INTO reserved_mismatch
  FROM balances b
  FULL JOIN open_reservations r USING (tenant_id, variant_id, warehouse_id, loc)
  WHERE coalesce(b.reserved, 0) <> coalesce(r.reserved, 0);

  SELECT count(*) INTO oversold FROM inventory.inventory_balances
  WHERE NOT allow_negative AND (on_hand < 0 OR reserved > on_hand OR reserved < 0);

  SELECT count(*) INTO bad_reservations FROM inventory.stock_reservations
  WHERE quantity - consumed_quantity - released_quantity < 0
     OR (status <> 'active' AND quantity - consumed_quantity - released_quantity <> 0);

  RAISE NOTICE 'I1 unknown type or wrong sign: %', unknown_types;
  RAISE NOTICE 'I2 balance.on_hand <> ledger: %', ledger_mismatch;
  RAISE NOTICE 'I3 balance.reserved <> open reservations: %', reserved_mismatch;
  RAISE NOTICE 'I4 oversold balances: %', oversold;
  RAISE NOTICE 'I5 inconsistent reservations: %', bad_reservations;
  IF unknown_types + ledger_mismatch + reserved_mismatch + oversold + bad_reservations > 0 THEN
    RAISE EXCEPTION 'STOCK INVARIANT VIOLATED (see notices above)';
  END IF;
  RAISE NOTICE 'OK: ledger = balances, reservations = reserved, no oversell';
END $$;
