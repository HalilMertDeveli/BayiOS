// Shared k6 helpers for the BayiOS load tests (#58): API client, synthetic tenant seeding, business flows.
// Everything is synthetic (example.test e-mails, "LOAD-" SKUs); nothing here is customer data.
import http from "k6/http";
import { check, fail, sleep } from "k6";
import { Counter } from "k6/metrics";

export const BASE_URL = (__ENV.BASE_URL || "http://127.0.0.1:5390").replace(/\/+$/, "");

/** Writes that were refused for a business reason the API is designed to give (not errors). */
export const businessRejections = new Counter("business_rejections");
/** 409 + Retry-After (row lock busy) that were retried. */
export const busyRetries = new Counter("busy_retries");
/** Unexpected responses (status the flow did not expect). Also visible as http_req_failed. */
export const unexpected = new Counter("unexpected_responses");

const JSON_HEADERS = { "Content-Type": "application/json", Accept: "application/json" };

export function etagOf(version) {
  return `W/"${version}"`;
}

/**
 * One request. `expect` lists the statuses this call may legitimately return; anything else is counted as unexpected
 * (and fails http_req_failed through the response callback). A 409 with Retry-After is retried up to 3 times.
 */
export function call(user, method, path, body, { tenantId, ifMatch, expect = [200, 201], name, tags = {} } = {}) {
  const headers = { ...JSON_HEADERS, Authorization: `Bearer ${user.token}` };
  if (tenantId) headers["X-Tenant-Id"] = tenantId;
  if (method !== "GET") headers["Idempotency-Key"] = crypto.randomUUID();
  if (ifMatch) headers["If-Match"] = ifMatch;
  const params = {
    headers,
    tags: { name: name || `${method} ${path.replace(/[0-9a-f-]{36}/g, "{id}").replace(/\?.*$/, "")}`, ...tags },
    responseCallback: http.expectedStatuses(...expect, 409),
    timeout: "30s",
  };
  const payload = body === undefined || body === null ? null : JSON.stringify(body);
  let response;
  for (let attempt = 0; attempt < 4; attempt++) {
    response = http.request(method, `${BASE_URL}${path}`, payload, params);
    const retryAfter = Number(response.headers["Retry-After"]);
    if (response.status === 409 && retryAfter > 0 && !expect.includes(409) && attempt < 3) {
      busyRetries.add(1);
      sleep(Math.min(retryAfter, 1));
      continue;
    }
    break;
  }
  if (!expect.includes(response.status)) {
    unexpected.add(1, { name: params.tags.name, status: String(response.status) });
    if (__ENV.LOAD_DEBUG) console.warn(`${method} ${path} -> ${response.status} ${response.body && response.body.slice(0, 300)}`);
  }
  return response;
}

export const json = (response) => {
  try {
    return response.json();
  } catch (_) {
    return null;
  }
};

export const problemCode = (response) => (json(response) || {}).code || "";

/** Like call() but aborts the iteration on an unexpected status (setup/seed steps). */
export function must(user, method, path, body, options = {}) {
  const response = call(user, method, path, body, options);
  const ok = (options.expect || [200, 201]).includes(response.status);
  if (!ok) fail(`${method} ${path} -> ${response.status}: ${String(response.body).slice(0, 500)}`);
  return json(response);
}

// tokens.json is written by tools/load/auth.mjs into the state dir, mounted at /state in the k6 container.
// open() is only allowed in the init context, hence at module level.
const TOKENS = JSON.parse(open(__ENV.TOKENS || "/state/tokens.json")).users;

export function loadUsers() {
  return TOKENS;
}

/**
 * Seeds one synthetic tenant through the public API: owner + invited manager / sales / warehouse_staff (real
 * invitation flow), a warehouse, `products` simple products with barcodes, a default price list, customers, and stock
 * via goods receipts (the ledger). `hot` products get `hotStock` units (reservation contention); the rest plenty.
 */
export function seedTenant(users, { slug, products = 200, hot = 10, hotStock = 30, coldStock = 100000, customers = 20, stock = true }) {
  const [owner, manager, sales, staff] = users;
  const tenant = must(owner, "POST", "/api/v1/tenants", { name: `Load ${slug}`, slug, sector: "general" });
  const t = { tenantId: tenant.id };
  must(owner, "POST", "/api/v1/sector-templates/general/apply", null, t);
  const settings = call(owner, "GET", "/api/v1/tenant/settings", null, t);
  must(owner, "PATCH", "/api/v1/tenant/settings", { negativeStockPolicy: "block" }, { ...t, ifMatch: settings.headers.Etag || settings.headers.ETag });

  for (const [user, role] of [[manager, "manager"], [sales, "sales"], [staff, "warehouse_staff"]]) {
    const issued = must(owner, "POST", "/api/v1/members/invitations", { email: user.email, role }, t);
    must(user, "POST", "/api/v1/invitations/accept", { token: issued.token });
  }

  const warehouse = must(owner, "POST", "/api/v1/warehouses", { code: "MRK", name: "Merkez (sentetik)", usesLocations: false }, t);
  const unitId = must(owner, "GET", "/api/v1/units", null, t).items[0].id;
  const words = ["vida", "somun", "conta", "rulman", "kablo", "led", "sigorta", "filtre", "kayış", "pul"];
  const variants = [];
  for (let i = 1; i <= products; i++) {
    const n = String(i).padStart(4, "0");
    const product = must(owner, "POST", "/api/v1/products", {
      name: `Load ürün ${n} ${words[i % words.length]}`,
      description: "Sentetik yük testi ürünü.",
      categoryId: null,
      brandId: null,
      defaultUnitId: unitId,
      taxRate: 20,
      status: null,
      sku: `LOAD-${n}`,
      barcode: `8690000${n.padStart(6, "0")}`,
      variants: null,
    }, t);
    const v = product.variants[0];
    variants.push({ id: v.id, sku: v.sku, barcode: v.barcode, hot: i <= hot, word: words[i % words.length] });
  }

  const list = must(owner, "POST", "/api/v1/price-lists", {
    name: "Liste (sentetik)", kind: "sales", currency: "TRY", isDefault: true, pricesIncludeTax: false,
    validFrom: null, validTo: null, basePriceListId: null, discountPercent: null,
  }, t);
  for (let i = 0; i < variants.length; i += 400) {
    must(owner, "PUT", `/api/v1/price-lists/${list.id}/items`, {
      items: variants.slice(i, i + 400).map((v, k) => ({ variantId: v.id, minQuantity: 1, unitPrice: 10 + ((i + k) % 90) })),
    }, t);
  }

  const customerIds = [];
  for (let i = 1; i <= customers; i++) {
    const created = must(owner, "POST", "/api/v1/customers", {
      code: `LC${String(i).padStart(3, "0")}`, name: `Load Bayi ${i} (sentetik)`, contactName: null, taxNumber: null, taxOffice: null,
      phone: null, email: null, whatsappNumbers: null, dealerGroupId: null, priceListId: null, paymentTermDays: 30,
      creditLimit: null, currency: null, notes: null,
    }, t);
    customerIds.push(created.customer.id);
  }

  if (stock) {
    for (let i = 0; i < variants.length; i += 100) {
      must(owner, "POST", "/api/v1/inventory/receipts", {
        warehouseId: warehouse.id,
        reference: `LOAD-IRS-${i / 100 + 1}`,
        lines: variants.slice(i, i + 100).map((v) => ({ variantId: v.id, locationId: null, quantity: v.hot ? hotStock : coldStock, unitCost: 5 })),
      }, t);
    }
  }
  return { tenantId: tenant.id, warehouseId: warehouse.id, variants, customerIds, users: { owner, manager, sales, staff } };
}

const pick = (items) => items[Math.floor(Math.random() * items.length)];

export function orderBody(world, lines) {
  return {
    customerId: pick(world.customerIds), warehouseId: world.warehouseId, source: "manual", sourceReferenceId: null,
    currency: null, pricesIncludeTax: null, requestedDeliveryDate: null, notes: "Yük testi (sentetik)",
    lines: lines.map(([variantId, quantity]) => ({ id: null, variantId, quantity, unitPrice: null, discountPercent: null, discountAmount: null })),
  };
}

/** Sales creates + submits, manager approves. Returns { detail, approved } — approved=false = stock refused (expected). */
export function createAndApprove(world, lines) {
  const t = { tenantId: world.tenantId };
  const { sales, manager } = world.users;
  let detail = must(sales, "POST", "/api/v1/orders", orderBody(world, lines), t);
  detail = must(sales, "POST", `/api/v1/orders/${detail.order.id}/submit`, { confirmNegativeStock: false }, { ...t, ifMatch: etagOf(detail.order.version) });
  const approve = call(manager, "POST", `/api/v1/orders/${detail.order.id}/approve`, { confirmNegativeStock: false },
    { ...t, ifMatch: etagOf(detail.order.version), expect: [200, 409, 422] });
  if (approve.status === 200) return { detail: json(approve), approved: true };
  const code = problemCode(approve);
  if (code.startsWith("inventory.insufficient")) {
    businessRejections.add(1, { reason: code });
  } else {
    unexpected.add(1, { name: "approve", status: `${approve.status} ${code}` });
  }
  return { detail, approved: false, code };
}

/** Approval → pick task (async job) → staff picks every line → complete → pack → confirm (stock leaves the ledger). */
export function fulfil(world, detail) {
  const t = { tenantId: world.tenantId };
  const { owner, staff } = world.users;
  let summary = null;
  for (let i = 0; i < 80 && !summary; i++) {
    const tasks = must(owner, "GET", `/api/v1/picking-tasks?orderId=${detail.order.id}`, null, { ...t, name: "GET /api/v1/picking-tasks?orderId" });
    summary = tasks.items.find((x) => x.type === "pick" && (x.status === "open" || x.status === "in_progress")) || null;
    if (!summary) sleep(0.25);
  }
  if (!summary) {
    unexpected.add(1, { name: "pick task never appeared", status: "timeout" });
    return false;
  }
  must(owner, "POST", `/api/v1/picking-tasks/${summary.id}/assign`, { userId: staff.sub }, t);
  must(staff, "POST", `/api/v1/picking-tasks/${summary.id}/start`, null, t);
  const task = must(staff, "GET", `/api/v1/picking-tasks/${summary.id}`, null, t);
  for (const line of task.lines) {
    must(staff, "POST", `/api/v1/picking-tasks/${summary.id}/scans`, { code: line.barcode || line.sku, location: line.locationCode || null, quantity: line.quantityToPick, lineId: line.id }, t);
  }
  const completed = must(staff, "POST", `/api/v1/picking-tasks/${summary.id}/complete`, null, t);
  const shipment = must(staff, "GET", `/api/v1/shipments/${completed.shipmentId}`, null, t);
  must(staff, "POST", `/api/v1/shipments/${shipment.id}/pack`, {
    packages: [{ items: shipment.lines.filter((l) => Number(l.quantity) > 0).map((l) => ({ code: null, shipmentLineId: l.id, quantity: l.quantity })) }],
  }, t);
  const confirmed = call(owner, "POST", `/api/v1/shipments/${shipment.id}/confirm`, { carrier: "Yük Kargo (sentetik)", trackingNumber: `LOAD-${shipment.id.slice(0, 8)}`, packageCount: 1 }, t);
  return check(confirmed, { "shipment confirmed": (r) => r.status === 200 });
}

export { pick };
