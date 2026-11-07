-- Restore drill seed (#55). SYNTHETIC data only (example.com addresses, +90 555 000 00 xx numbers, invented names);
-- runs as the container superuser on the throwaway drill SOURCE database, never on a real environment.
-- Two tenants, consistent with the domain rules: balances = sum of ledger movements, reserved = open reservations of
-- approved orders, one audit event per approval. Deterministic ids (md5 of a label) so reruns compare equal.
\set ON_ERROR_STOP on
BEGIN;

CREATE FUNCTION pg_temp.u(label text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5(label)::uuid $$;

CREATE TEMP TABLE drill_tenant (code text PRIMARY KEY, id uuid, slug text, name text, sector text, products int, customers int, orders int, receipt numeric) ON COMMIT DROP;
INSERT INTO drill_tenant VALUES
    ('a', '0b0a0000-0000-4000-8000-00000000000a', 'drill-a', 'Drill Motor Yedek Parça (sentetik)', 'motorcycle_parts', 40, 10, 8, 100),
    ('b', '0b0b0000-0000-4000-8000-00000000000b', 'drill-b', 'Drill LED Aydınlatma (sentetik)', 'led_electronics', 25, 6, 5, 50);

INSERT INTO identity.tenants (id, name, slug, status, sector, default_currency, locale, time_zone, row_version, created_at)
SELECT id, name, slug, 'active', sector, 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now() - interval '30 days' FROM drill_tenant;

INSERT INTO identity.tenant_settings (tenant_id, negative_stock_policy, sales_can_approve_orders, sales_max_discount_percent,
    show_cost_to_manager, show_cost_to_viewer, staff_can_assign_barcode, staff_can_approve_receipt, require2fa, row_version, created_at)
SELECT id, 'block', false, 10, true, false, true, false, false, 1, now() FROM drill_tenant;

INSERT INTO identity.user_profiles (user_id, email, full_name, created_at)
SELECT pg_temp.u('user-' || code || '-' || r), format('drill-%s-%s@example.com', r, code), format('Drill %s %s', initcap(r), upper(code)), now()
FROM drill_tenant, unnest(ARRAY['owner', 'sales']) r;

INSERT INTO identity.memberships (id, tenant_id, user_id, role, status, created_at)
SELECT pg_temp.u('membership-' || code || '-' || r), id, pg_temp.u('user-' || code || '-' || r), r, 'active', now()
FROM drill_tenant, unnest(ARRAY['owner', 'sales']) r;

INSERT INTO catalog.units (id, code, name, allows_fraction, tenant_id, row_version, created_at)
SELECT pg_temp.u('unit-' || code), 'adet', 'Adet', false, id, 1, now() FROM drill_tenant;

INSERT INTO catalog.brands (id, name, tenant_id, row_version, created_at)
SELECT pg_temp.u('brand-' || code || '-' || b), format('Drill Marka %s%s', upper(code), b), id, 1, now()
FROM drill_tenant, generate_series(1, 2) b;

INSERT INTO catalog.products (id, name, brand_id, default_unit_id, tax_rate, status, attributes, tenant_id, row_version, created_at)
SELECT pg_temp.u('product-' || code || '-' || i), format('Drill ürün %s-%s', upper(code), lpad(i::text, 3, '0')),
       pg_temp.u('brand-' || code || '-' || (1 + i % 2)), pg_temp.u('unit-' || code), 20, 'active', '{}', id, 1, now()
FROM drill_tenant, generate_series(1, products) i;

INSERT INTO catalog.product_variants (id, product_id, sku, barcode, option_values, unit_id, status, attributes, tenant_id, row_version, created_at)
SELECT pg_temp.u('variant-' || code || '-' || i), pg_temp.u('product-' || code || '-' || i), format('%s-%s', upper(code), lpad(i::text, 4, '0')),
       format('869000%s%s', CASE code WHEN 'a' THEN 1 ELSE 2 END, lpad(i::text, 6, '0')), '{}', pg_temp.u('unit-' || code), 'active', '{}', id, 1, now()
FROM drill_tenant, generate_series(1, products) i;

INSERT INTO warehouse.warehouses (id, code, name, uses_locations, is_default, status, tenant_id, row_version, created_at)
SELECT pg_temp.u('warehouse-' || code), 'MERKEZ', 'Merkez Depo (sentetik)', false, true, 'active', id, 1, now() FROM drill_tenant;

-- Ledger: a receipt per variant, and a damage write-off on every 5th variant.
INSERT INTO inventory.inventory_movements (id, tenant_id, variant_id, warehouse_id, type, quantity, unit_cost, source_type, source_id, balance_after, correlation_id, occurred_at, created_by)
SELECT pg_temp.u('receipt-' || code || '-' || i), id, pg_temp.u('variant-' || code || '-' || i), pg_temp.u('warehouse-' || code),
       'receipt', receipt, 10, 'receipt', pg_temp.u('receipt-doc-' || code), receipt, 'drill', now() - interval '10 days', pg_temp.u('user-' || code || '-owner')
FROM drill_tenant, generate_series(1, products) i;
INSERT INTO inventory.inventory_movements (id, tenant_id, variant_id, warehouse_id, type, quantity, source_type, reason, balance_after, correlation_id, occurred_at, created_by)
SELECT pg_temp.u('damage-' || code || '-' || i), id, pg_temp.u('variant-' || code || '-' || i), pg_temp.u('warehouse-' || code),
       'damage_out', -3, 'adjustment', 'damaged', receipt - 3, 'drill', now() - interval '5 days', pg_temp.u('user-' || code || '-owner')
FROM drill_tenant, generate_series(5, products, 5) i;

INSERT INTO customers.customers (id, code, name, contact_name, phone, email, payment_term_days, currency, status, tenant_id, row_version, created_at)
SELECT pg_temp.u('customer-' || code || '-' || i), format('C-%s', lpad(i::text, 3, '0')), format('Drill Müşteri %s-%s Ltd.', upper(code), i),
       format('Yetkili %s%s', upper(code), i), format('+90 555 000 %s %s', CASE code WHEN 'a' THEN '01' ELSE '02' END, lpad(i::text, 2, '0')),
       format('musteri-%s%s@example.com', code, i), 30, 'TRY', 'active', id, 1, now()
FROM drill_tenant, generate_series(1, customers) i;

-- Approved orders, two lines of 5 each, reserved in full.
INSERT INTO orders.orders (id, tenant_id, number, customer_id, warehouse_id, status, fulfillment_status, source, currency, prices_include_tax, price_date,
    subtotal, discount_total, tax_total, grand_total, submitted_at, approved_at, approved_by, row_version, created_at)
SELECT pg_temp.u('order-' || code || '-' || k), id, format('%s-2026-%s', upper(code), lpad(k::text, 6, '0')),
       pg_temp.u('customer-' || code || '-' || (1 + (k - 1) % customers)), pg_temp.u('warehouse-' || code), 'approved', 'none', 'manual', 'TRY', false, current_date,
       1000, 0, 200, 1200, now() - interval '2 days', now() - interval '1 day', pg_temp.u('user-' || code || '-owner'), 2, now() - interval '2 days'
FROM drill_tenant, generate_series(1, orders) k;

INSERT INTO orders.order_lines (id, tenant_id, order_id, line_no, variant_id, sku, name, unit, quantity, unit_price, price_source, price_overridden, tax_rate,
    line_subtotal, line_discount, line_total, qty_reserved, qty_shipped, qty_cancelled, created_at)
SELECT pg_temp.u('line-' || code || '-' || k || '-' || n), id, pg_temp.u('order-' || code || '-' || k), n, pg_temp.u('variant-' || code || '-' || (k + n - 1)),
       format('%s-%s', upper(code), lpad((k + n - 1)::text, 4, '0')), format('Drill ürün %s-%s', upper(code), lpad((k + n - 1)::text, 3, '0')), 'adet',
       5, 100, 'list', false, 20, 500, 0, 500, 5, 0, 0, now() - interval '2 days'
FROM drill_tenant, generate_series(1, orders) k, generate_series(1, 2) n;

INSERT INTO orders.order_status_history (id, tenant_id, order_id, action, from_status, to_status, fulfillment_status, changed_by, changed_at, correlation_id)
SELECT pg_temp.u('history-' || code || '-' || k), id, pg_temp.u('order-' || code || '-' || k), 'approve', 'draft', 'approved', 'none',
       pg_temp.u('user-' || code || '-owner'), now() - interval '1 day', 'drill'
FROM drill_tenant, generate_series(1, orders) k;

INSERT INTO inventory.stock_reservations (id, tenant_id, order_id, order_line_id, variant_id, warehouse_id, quantity, consumed_quantity, released_quantity, status, correlation_id, created_at, created_by)
SELECT pg_temp.u('reservation-' || l.id), l.tenant_id, l.order_id, l.id, l.variant_id, o.warehouse_id, l.quantity, 0, 0, 'active', 'drill', now() - interval '1 day', o.approved_by
FROM orders.order_lines l JOIN orders.orders o ON o.id = l.order_id
WHERE l.tenant_id IN (SELECT id FROM drill_tenant);

-- Derived balances, exactly as the stock engine keeps them (same transaction, from the ledger and the reservations).
INSERT INTO inventory.inventory_balances (id, tenant_id, variant_id, warehouse_id, on_hand, reserved, allow_negative, version, created_at, updated_at)
SELECT pg_temp.u('balance-' || m.variant_id), m.tenant_id, m.variant_id, m.warehouse_id, sum(m.quantity),
       coalesce((SELECT sum(r.quantity) FROM inventory.stock_reservations r WHERE r.variant_id = m.variant_id AND r.status = 'active'), 0),
       false, 1, now(), now()
FROM inventory.inventory_movements m
WHERE m.tenant_id IN (SELECT id FROM drill_tenant)
GROUP BY m.tenant_id, m.variant_id, m.warehouse_id;

INSERT INTO audit.audit_events (id, tenant_id, actor_user_id, actor_role, action, entity_type, entity_id, changes, correlation_id, occurred_at)
SELECT pg_temp.u('audit-' || code || '-' || k), id, pg_temp.u('user-' || code || '-owner'), 'owner', 'order.approved', 'order', pg_temp.u('order-' || code || '-' || k),
       '{"lines": 2}', 'drill', now() - interval '1 day'
FROM drill_tenant, generate_series(1, orders) k;

INSERT INTO platform.jobs (id, tenant_id, type, payload, status, priority, run_after, attempts, max_attempts, correlation_id, created_at, finished_at)
SELECT pg_temp.u('job-' || code || '-' || j), id, 'drill.synthetic', '{}', 'succeeded', 0, now() - interval '1 hour', 1, 5, 'drill', now() - interval '1 hour', now()
FROM drill_tenant, generate_series(1, 3) j;

COMMIT;

SELECT format('seeded: %s tenants, %s products, %s movements, %s orders, %s reservations, %s audit events',
    (SELECT count(*) FROM identity.tenants), (SELECT count(*) FROM catalog.products), (SELECT count(*) FROM inventory.inventory_movements),
    (SELECT count(*) FROM orders.orders), (SELECT count(*) FROM inventory.stock_reservations), (SELECT count(*) FROM audit.audit_events)) AS seed;
