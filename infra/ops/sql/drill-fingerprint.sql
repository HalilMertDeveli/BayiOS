-- Restore drill fingerprint (#55): one line per tenant with counts, ledger sums and a content hash, compared between
-- the source and the restored database. Admin session (sees all tenants). Synthetic drill data only.
SELECT string_agg(line, ' | ' ORDER BY line)
FROM (
    SELECT format(
        '%s products=%s variants=%s movements=%s ledger_sum=%s on_hand=%s reserved=%s orders=%s order_total=%s customers=%s audit=%s hash=%s',
        t.slug,
        (SELECT count(*) FROM catalog.products p WHERE p.tenant_id = t.id),
        (SELECT count(*) FROM catalog.product_variants v WHERE v.tenant_id = t.id),
        (SELECT count(*) FROM inventory.inventory_movements m WHERE m.tenant_id = t.id),
        (SELECT coalesce(sum(m.quantity), 0) FROM inventory.inventory_movements m WHERE m.tenant_id = t.id),
        (SELECT coalesce(sum(b.on_hand), 0) FROM inventory.inventory_balances b WHERE b.tenant_id = t.id),
        (SELECT coalesce(sum(b.reserved), 0) FROM inventory.inventory_balances b WHERE b.tenant_id = t.id),
        (SELECT count(*) FROM orders.orders o WHERE o.tenant_id = t.id),
        (SELECT coalesce(sum(o.grand_total), 0) FROM orders.orders o WHERE o.tenant_id = t.id),
        (SELECT count(*) FROM customers.customers c WHERE c.tenant_id = t.id),
        (SELECT count(*) FROM audit.audit_events a WHERE a.tenant_id = t.id),
        left(md5(
            coalesce((SELECT string_agg(v.sku || '/' || v.id, ',' ORDER BY v.sku) FROM catalog.product_variants v WHERE v.tenant_id = t.id), '') ||
            coalesce((SELECT string_agg(m.id || '/' || m.quantity || '/' || m.balance_after, ',' ORDER BY m.id) FROM inventory.inventory_movements m WHERE m.tenant_id = t.id), '') ||
            coalesce((SELECT string_agg(o.number || '/' || o.status || '/' || o.grand_total, ',' ORDER BY o.number) FROM orders.orders o WHERE o.tenant_id = t.id), '')
        ), 12)) AS line
    FROM identity.tenants t
) per_tenant;
