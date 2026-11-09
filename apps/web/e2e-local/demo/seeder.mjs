import { Api, ensureUser, etagOf, sleep } from "./stack.mjs";
import { buildXlsx } from "./xlsx.mjs";
import { genelDemo } from "./data-genel.mjs";
import { ledDemo } from "./data-led.mjs";
import { motorDemo } from "./data-motor.mjs";

/**
 * Demo seeder (#56/#57 + the generic default demo). Builds a fully synthetic business ONLY through the public BayiOS
 * API, as its owner (a real Supabase Auth user): tenant + template (plus own units, categories and custom fields where
 * the template has none), warehouses and shelves, catalog with variants, custom-field attributes and barcodes, price
 * lists (default + dealer lists with tiers), dealer groups, customers with addresses, team members invited through the
 * Members API (each signs up and accepts), an XLSX catalog import, goods receipts, orders driven through the real flows
 * (submit/approve → prioritised and assigned pick tasks → pick scans → pack check → shipment confirm, shortage +
 * short-close, cancel), one WhatsApp message parsed by the deterministic rules parser into a draft, shelf moves,
 * two-stage warehouse transfers, an open stock count and minimum-stock thresholds. Re-running against an already
 * complete demo tenant (same slug) changes nothing.
 */

/** `genel` (Demo Toptan Ticaret, general template) is the default demo; motor and led are optional sector examples. */
export const DEMOS = { genel: genelDemo, motor: motorDemo, led: ledDemo };
export const DEFAULT_DEMO = "genel";

const ORDER_TARGETS = ["draft", "pending", "approved", "picking", "packed", "partial_closed", "shipped", "completed", "cancelled", "cancelled_draft"];
const round2 = (n) => Math.round(n * 100) / 100;

/** The fields of a template custom field, used for XLSX headers (label + unit). */
async function customFieldHeaders(api) {
  const fields = (await api.get("/api/v1/custom-fields?entityType=product")).items;
  return new Map(fields.map((f) => [f.key, f.unit ? `${f.label} (${f.unit})` : f.label]));
}

/** Counts of everything the demo contains (also used for the "already seeded" check). */
export async function summarize(api) {
  const [products, customers, orders, warehouses, priceLists, dealerGroups, imports, parses] = await Promise.all([
    api.all("/api/v1/products"),
    api.all("/api/v1/customers"),
    api.all("/api/v1/orders"),
    api.get("/api/v1/warehouses"),
    api.get("/api/v1/price-lists"),
    api.get("/api/v1/dealer-groups"),
    api.get("/api/v1/import-jobs?limit=50"),
    api.get("/api/v1/ai/order-parses?limit=50"),
  ]);
  const locations = await Promise.all(warehouses.items.map((w) => (w.usesLocations ? api.get(`/api/v1/warehouses/${w.id}/locations`) : { items: [] })));
  const byStatus = {};
  for (const o of orders) byStatus[o.status] = (byStatus[o.status] ?? 0) + 1;
  const partial = orders.filter((o) => o.fulfillmentStatus === "partial").length;
  // Billing address on the order (#136): a snapshot from submit on, the customer's default as preview on drafts.
  let withBilling = 0;
  for (const o of orders) if ((await api.get(`/api/v1/orders/${o.id}`)).order.billingAddress) withBilling++;
  // Minimum stock thresholds (#137) and the critical-stock report in thresholds mode (all warehouses).
  const thresholds = await api.all("/api/v1/inventory/thresholds");
  const critical = await api.all("/api/v1/reports/critical-stock");
  const criticalWithOutOfStock = await api.all("/api/v1/reports/critical-stock?includeOutOfStock=true");
  // Team, picking work queue, two-stage transfers and stock counts (#129, #98, #168).
  const [members, tasks, transfers, stockCounts] = await Promise.all([
    api.all("/api/v1/members"),
    api.all("/api/v1/picking-tasks"),
    api.all("/api/v1/stock-transfers"),
    api.all("/api/v1/stock-counts"),
  ]);
  const activeTasks = tasks.filter((t) => ["open", "in_progress"].includes(t.status));
  return {
    products: products.length,
    variants: products.reduce((n, p) => n + p.variants.length, 0),
    barcodes: products.reduce((n, p) => n + p.variants.filter((v) => v.barcode).length, 0),
    warehouses: warehouses.items.length,
    locations: locations.reduce((n, l) => n + l.items.length, 0),
    priceLists: priceLists.items.length,
    dealerGroups: dealerGroups.items.length,
    customers: customers.length,
    importJobs: imports.items.filter((j) => j.status === "completed").length,
    whatsappParses: parses.items.length,
    orders: orders.length,
    ordersByStatus: byStatus,
    partialFulfilment: partial,
    ordersWithBillingAddress: withBilling,
    stockThresholds: thresholds.length,
    criticalStock: critical.length,
    criticalStockWithOutOfStock: criticalWithOutOfStock.length,
    whatsappOrders: orders.filter((o) => o.source === "whatsapp_ai").length,
    membersByRole: tally(members.filter((m) => m.status === "active"), (m) => m.role),
    activeTasksByPriority: tally(activeTasks, (t) => t.priority),
    assignedActiveTasks: activeTasks.filter((t) => t.assignedTo).length,
    transfersByStatus: tally(transfers, (t) => t.status),
    transfersWithDiscrepancy: transfers.filter((t) => t.hasDiscrepancy).length,
    stockCountsByStatus: tally(stockCounts, (c) => c.status),
  };
}

/** { key → count }, keys sorted (stable JSON for the idempotency comparison in CI). */
function tally(items, key) {
  const out = {};
  for (const k of items.map(key).sort()) out[k] = (out[k] ?? 0) + 1;
  return out;
}

/**
 * Uploads the demo's supplier catalog as a generated XLSX and runs it through the Imports API
 * (upload → explicit mapping → dry-run validation → execute), prices into the given list.
 * @returns {Promise<{ job: any, variants: Record<string, { id: string, barcode: string | null }> }>}
 */
export async function runCatalogImport(api, def, priceListId, log = () => {}) {
  const rows = def.importProducts;
  const labels = await customFieldHeaders(api);
  const attrKeys = [...new Set(rows.flatMap((r) => Object.keys(r.attributes ?? {})))].filter((k) => labels.has(k));
  const headers = ["Stok Kodu", "Barkod", "Ürün Adı", "Birim", "KDV", "Kategori", "Marka", "Fiyat", ...attrKeys.map((k) => labels.get(k))];
  const table = [
    headers,
    ...rows.map((r) => [r.sku, r.barcode, r.name, r.unit, r.taxRate, r.category, r.brand, r.price, ...attrKeys.map((k) => r.attributes?.[k] ?? null)]),
  ];
  const fileName = `${def.code}-tedarikci-katalogu-sentetik.xlsx`;
  const form = new FormData();
  form.append("type", "products");
  form.append("file", new Blob([buildXlsx("Katalog", table)], { type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }), fileName);
  const uploaded = (await api.raw("POST", "/api/v1/import-jobs", { form, expect: [200, 201] })).body;
  log(`  import ${uploaded.id}: ${uploaded.totalRows} rows uploaded (${uploaded.fileFormat})`);

  const columns = { sku: "Stok Kodu", barcode: "Barkod", name: "Ürün Adı", unit: "Birim", taxRate: "KDV", category: "Kategori", brand: "Marka", price: "Fiyat" };
  for (const key of attrKeys) columns[`attr.${key}`] = labels.get(key);
  await api.put(`/api/v1/import-jobs/${uploaded.id}/mapping`, { columns, matchKey: "sku", mode: "upsert", priceListId, saveAsTemplate: `${def.tenant.name} tedarikçi şablonu` });

  const waitFor = async (statuses) => {
    for (let i = 0; i < 240; i++) {
      const job = await api.get(`/api/v1/import-jobs/${uploaded.id}`);
      if (statuses.includes(job.status)) return job;
      if (job.status === "failed" || job.status === "cancelled") throw new Error(`Import ${uploaded.id} ${job.status}: ${JSON.stringify(job.failure)}`);
      await sleep(500);
    }
    throw new Error(`Import ${uploaded.id} did not reach ${statuses.join("/")} in time`);
  };
  await api.post(`/api/v1/import-jobs/${uploaded.id}/validate`);
  const validated = await waitFor(["validated"]);
  if (validated.preview.error > 0) {
    const rowsWithErrors = await api.get(`/api/v1/import-jobs/${uploaded.id}/rows?status=error`).catch(() => null);
    throw new Error(`Import dry run found ${validated.preview.error} invalid rows: ${JSON.stringify(rowsWithErrors)?.slice(0, 2000)}`);
  }
  await api.post(`/api/v1/import-jobs/${uploaded.id}/start`);
  const job = await waitFor(["completed"]);
  log(`  import completed: ${job.progress.created} created, ${job.progress.updated} updated, ${job.progress.failed} failed`);

  const variants = {};
  for (const row of rows) {
    const found = (await api.get(`/api/v1/variants?sku=${encodeURIComponent(row.sku)}`)).items.find((v) => v.sku === row.sku);
    if (!found) throw new Error(`Imported SKU ${row.sku} not found in the catalog`);
    variants[row.sku] = { id: found.id, productId: found.productId, barcode: found.barcode, price: row.price, unit: row.unit, category: row.category };
  }
  return { job, variants };
}

/** Owner's own Supabase token → API client (no tenant yet) + user id. */
export async function signInOwner(cfg, owner) {
  const token = await ensureUser(cfg, owner);
  const api = new Api(cfg, token);
  const me = await api.get("/api/v1/me");
  return { api, me };
}

/**
 * @param {object} p
 * @param {any} p.cfg stackConfig()
 * @param {"genel" | "motor" | "led"} p.demo
 * @param {"full" | "small"} [p.size]
 * @param {{ email: string, password: string, fullName: string }} p.owner
 * @param {{ role: string, email: string, password: string, fullName: string }[]} [p.team] invited members (signed up
 *   through Supabase Auth + Mailpit, then accept); without it picking tasks go to the owner
 * @param {{ name: string, slug: string }} [p.tenant] overrides the demo's own name/slug (E2E: unique per run)
 * @param {{ importCatalog?: boolean, orders?: boolean, whatsapp?: boolean }} [p.include]
 * @param {(line: string) => void} [p.log]
 */
export async function seedDemo({ cfg, demo, size = "full", owner, team = [], tenant, include = {}, log = () => {} }) {
  const def = DEMOS[demo](size);
  const tenantDef = tenant ?? def.tenant;
  const want = { importCatalog: true, orders: true, whatsapp: true, ...include };
  const started = Date.now();

  const { api: userApi, me } = await signInOwner(cfg, owner);
  const existing = me.memberships.find((m) => m.tenantSlug === tenantDef.slug);
  if (existing) {
    const api = userApi.withTenant(existing.tenantId);
    const counts = await summarize(api);
    const expectedOrders = (want.orders ? def.orders.length : 0) + (want.whatsapp ? 1 : 0);
    if (counts.orders >= expectedOrders && counts.products >= def.products.length) {
      log(`${tenantDef.slug}: already seeded — nothing changed.`);
      return { skipped: true, def, tenantId: existing.tenantId, api, userId: me.userId, counts, seconds: (Date.now() - started) / 1000 };
    }
    throw new Error(
      `${tenantDef.slug} exists but is incomplete (${counts.products} products, ${counts.orders} orders) — a previous run was interrupted. ` +
        "Business data lives in the throw-away Postgres of the local stack: restart it (bash infra/ci/e2e-stack.sh up) and seed again.",
    );
  }

  log(`${tenantDef.slug}: creating "${tenantDef.name}" (${def.sector}, ${size})`);
  const created = await userApi.post("/api/v1/tenants", { name: tenantDef.name, slug: tenantDef.slug, sector: def.sector });
  const api = userApi.withTenant(created.id);
  await api.post(`/api/v1/sector-templates/${def.sector}/apply`);
  await api.patch("/api/v1/tenant/settings", { negativeStockPolicy: "block", defaultTaxRate: def.taxRate, pricesIncludeTaxDisplay: false });

  // ---- Team (#129): the owner invites each member, who signs up, confirms the e-mail and accepts the invitation.
  const members = await addTeam(cfg, api, team, created.id, log);

  // ---- Warehouses and shelves (the first one becomes the default warehouse). Shelves are keyed by category (or key).
  const warehouses = {};
  for (const w of def.warehouses) {
    const wh = await api.post("/api/v1/warehouses", { code: w.code, name: w.name, usesLocations: w.usesLocations });
    const locations = {};
    for (const l of w.locations) {
      locations[l.category ?? l.key] = { code: l.code, id: (await api.post(`/api/v1/warehouses/${wh.id}/locations`, { code: l.code, barcode: null, pickSequence: l.pickSequence })).id };
    }
    warehouses[w.code] = { id: wh.id, name: w.name, usesLocations: w.usesLocations, locations };
  }
  const [mainCode, shopCode] = def.warehouses.map((w) => w.code);

  // ---- Reference data from the template, plus the demo's own units / categories / custom fields (the general
  // template ships only adet, koli and kg) + brands.
  const units = Object.fromEntries((await api.get("/api/v1/units")).items.map((u) => [u.code, u.id]));
  for (const u of def.units ?? []) {
    if (!units[u.code]) units[u.code] = (await api.post("/api/v1/units", { code: u.code, name: u.name, allowsFraction: u.allowsFraction })).id;
  }
  const categories = Object.fromEntries((await api.get("/api/v1/categories")).items.map((c) => [c.name, c.id]));
  for (const [i, name] of (def.categories ?? []).entries()) {
    if (!categories[name]) categories[name] = (await api.post("/api/v1/categories", { name, parentId: null, sortOrder: (i + 1) * 10 })).id;
  }
  for (const [i, f] of (def.customFields ?? []).entries()) {
    await api.post("/api/v1/custom-fields", { ...f, required: false, sortOrder: (i + 1) * 10 });
  }
  const brands = {};
  for (const name of def.brands) brands[name] = (await api.post("/api/v1/brands", { name })).id;
  const unitId = (code) => units[code] ?? (() => { throw new Error(`Unit ${code} missing from the ${def.sector} template`); })();

  // ---- Catalog: products with variants, attributes and barcodes.
  const variants = {};
  for (const p of def.products) {
    const simple = p.variants.length === 1 && !p.variants[0].name;
    const body = {
      name: p.name,
      description: "Sentetik demo ürünü — gerçek bir ürün değildir.",
      categoryId: categories[p.category] ?? null,
      brandId: brands[p.brand] ?? null,
      defaultUnitId: unitId(p.unit),
      taxRate: def.taxRate,
      status: null,
      attributes: p.attributes,
      ...(simple
        ? { sku: p.variants[0].sku, barcode: p.variants[0].barcode, variants: null, variantAttributes: p.variants[0].attributes ?? null }
        : {
            sku: null,
            barcode: null,
            variants: p.variants.map((v) => ({ sku: v.sku, barcode: v.barcode, name: v.name, optionValues: v.optionValues, unitId: null, status: null, attributes: v.attributes ?? null })),
          }),
    };
    const product = await api.post("/api/v1/products", body);
    for (const v of p.variants) {
      const createdVariant = product.variants.find((x) => x.sku === v.sku);
      variants[v.sku] = { id: createdVariant.id, productId: product.id, barcode: v.barcode, price: v.price, unit: p.unit, category: p.category, name: p.name };
    }
  }
  log(`  catalog: ${def.products.length} products, ${Object.keys(variants).length} variants`);

  // ---- Price lists: default list (with quantity tiers) + dealer lists derived from it (discount + own tiers).
  const listBody = (name, extra) => ({ name, kind: "sales", currency: "TRY", isDefault: false, pricesIncludeTax: false, validFrom: null, validTo: null, basePriceListId: null, discountPercent: null, ...extra });
  const defaultList = await api.post("/api/v1/price-lists", listBody(def.priceLists.defaultName, { isDefault: true }));
  const putItems = async (listId, items) => {
    for (let i = 0; i < items.length; i += 400) await api.put(`/api/v1/price-lists/${listId}/items`, { items: items.slice(i, i + 400) });
  };
  const defaultItems = [];
  for (const p of def.products) {
    for (const v of p.variants) {
      defaultItems.push({ variantId: variants[v.sku].id, minQuantity: 1, unitPrice: v.price });
      for (const t of p.tiers ?? []) defaultItems.push({ variantId: variants[v.sku].id, minQuantity: t.minQuantity, unitPrice: round2(v.price * t.factor) });
    }
  }
  await putItems(defaultList.id, defaultItems);
  const dealerLists = {};
  for (const d of def.priceLists.dealer) {
    const list = await api.post("/api/v1/price-lists", listBody(d.name, { basePriceListId: defaultList.id, discountPercent: d.discountPercent }));
    const tiers = d.tiers.filter((t) => variants[t.sku]).map((t) => ({ variantId: variants[t.sku].id, minQuantity: t.minQuantity, unitPrice: round2(variants[t.sku].price * t.factor) }));
    if (tiers.length) await putItems(list.id, tiers);
    dealerLists[d.group] = { id: list.id, name: d.name, discountPercent: d.discountPercent };
  }

  // ---- Dealer groups and customers with addresses.
  const dealerGroups = {};
  for (const name of def.dealerGroups) {
    dealerGroups[name] = (await api.post("/api/v1/dealer-groups", { name, priceListId: dealerLists[name]?.id ?? null, defaultDiscountPercent: null })).id;
  }
  const customers = [];
  for (const c of def.customers) {
    const { customer } = await api.post("/api/v1/customers", {
      code: c.code,
      name: c.name,
      contactName: c.contactName,
      taxNumber: null,
      taxOffice: null,
      phone: c.phone,
      email: c.email,
      whatsappNumbers: null,
      dealerGroupId: dealerGroups[c.group] ?? null,
      priceListId: null,
      paymentTermDays: 30,
      creditLimit: null,
      currency: null,
      notes: "Sentetik demo müşterisi.",
    });
    const address = await api.post(`/api/v1/customers/${customer.id}/addresses`, { type: "shipping", isDefault: true, line2: null, ...c.address });
    const billing = await api.post(`/api/v1/customers/${customer.id}/addresses`, { type: "billing", isDefault: true, line2: null, ...c.billingAddress });
    customers.push({ ...c, id: customer.id, addressId: address.id, billingAddressId: billing.id });
  }
  log(`  ${customers.length} customers in ${def.dealerGroups.length} dealer groups, ${1 + def.priceLists.dealer.length} price lists`);

  // ---- Supplier catalog via the Imports API (generated XLSX).
  let importJob = null;
  if (want.importCatalog && def.importProducts.length) {
    const result = await runCatalogImport(api, def, defaultList.id, log);
    importJob = result.job;
    Object.assign(variants, result.variants);
  }

  // ---- Stock through goods receipts (ledger): every variant on its category shelf, enough for the scripted orders.
  const demand = {};
  for (const o of want.orders ? def.orders : []) for (const [sku, qty] of o.lines) demand[sku] = (demand[sku] ?? 0) + qty;
  for (const l of want.whatsapp ? def.whatsapp.lines : []) demand[l.sku] = (demand[l.sku] ?? 0) + l.quantity;
  const stock = {};
  for (const [sku, v] of Object.entries(variants)) {
    const s = def.stock(v, v.category); // always called: keeps the seeded RNG sequence identical
    const main = def.lowStock.includes(sku) ? 2 : s.main;
    if (def.outOfStock?.includes(sku)) {
      if (demand[sku]) throw new Error(`${sku} is out of stock by design but an order or WhatsApp line needs it`);
      stock[sku] = { main: 0, shop: 0 };
      continue;
    }
    stock[sku] = { main: Math.max(main, Math.ceil(demand[sku] ?? 0) + (demand[sku] ? 5 : 0)), shop: s.shop };
  }
  const receive = async (code, lines, reference) => {
    for (let i = 0; i < lines.length; i += 100) {
      await api.post("/api/v1/inventory/receipts", { warehouseId: warehouses[code].id, reference: `${reference}-${i / 100 + 1}`, lines: lines.slice(i, i + 100) });
    }
  };
  const mainShelf = (v) => (warehouses[mainCode].usesLocations ? (warehouses[mainCode].locations[v.category]?.id ?? Object.values(warehouses[mainCode].locations)[0].id) : null);
  await receive(
    mainCode,
    Object.entries(stock)
      .filter(([, s]) => s.main > 0)
      .map(([sku, s]) => ({ variantId: variants[sku].id, locationId: mainShelf(variants[sku]), quantity: s.main, unitCost: round2(variants[sku].price * 0.6) })),
    "IRS-DEMO-MERKEZ",
  );
  const shopLines = Object.entries(stock).filter(([, s]) => s.shop > 0).map(([sku, s]) => ({ variantId: variants[sku].id, locationId: null, quantity: s.shop, unitCost: round2(variants[sku].price * 0.6) }));
  if (shopCode && shopLines.length) await receive(shopCode, shopLines, "IRS-DEMO-SUBE");
  log(`  stock received: ${Object.keys(stock).length} variants in ${mainCode}${shopLines.length ? `, ${shopLines.length} in ${shopCode}` : ""}`);

  const refs = { def, api, tenantId: created.id, userId: me.userId, members, warehouses, variants, stock, demand, customers, priceLists: { default: defaultList.id, dealer: dealerLists }, dealerGroups, importJob };

  // ---- Orders in every state, through the real flows.
  const orders = [];
  if (want.orders) {
    for (const [index, spec] of def.orders.entries()) {
      if (!ORDER_TARGETS.includes(spec.target)) throw new Error(`Unknown order target ${spec.target}`);
      const order = await driveOrder(refs, spec, index);
      orders.push(order);
    }
    log(`  orders: ${orders.map((o) => `${o.number}=${o.status}`).join(", ")}`);
  }

  // ---- One WhatsApp message → deterministic rules parser (AI off) → human-confirmed draft.
  let whatsapp = null;
  if (want.whatsapp) {
    whatsapp = await whatsappDraft(refs);
    log(`  whatsapp: parse ${whatsapp.parseId} (${whatsapp.parser}) → draft ${whatsapp.orderNumber}`);
  }

  // ---- Warehouse documents (#168): shelf moves, two-stage transfers, an open stock count. Before the thresholds, so
  // the critical-stock report reflects the final balances.
  const relocations = await seedRelocations(refs);
  if (relocations.length) log(`  shelf moves: ${relocations.map((r) => `${r.sku} ${r.quantity} → ${r.to}`).join(", ")}`);
  const transfers = await seedTransfers(refs);
  if (transfers.length) log(`  transfers: ${transfers.map((t) => `${t.number}=${t.status}${t.hasDiscrepancy ? " (fark)" : ""}`).join(", ")}`);
  const stockCount = await seedStockCount(refs);
  if (stockCount) log(`  stock count ${stockCount.number} (${stockCount.status}) on ${stockCount.shelf}: ${stockCount.counted}/${stockCount.lines} lines counted`);

  // ---- Minimum stock thresholds (#137): a meaningful critical-stock tile and report.
  const thresholds = await seedThresholds(refs);
  log(`  thresholds: ${thresholds.critical.map((c) => `${c.sku} ${c.available}≤${c.min}`).join(", ")}; override ${thresholds.override.sku} @${thresholds.override.warehouse} → ${thresholds.override.min}; no threshold, 0 stock: ${def.outOfStock?.join(", ") ?? "—"}`);

  const counts = await summarize(api);
  return { skipped: false, ...refs, orders, whatsapp, relocations, transfers, stockCount, thresholds, counts, seconds: (Date.now() - started) / 1000 };
}

/**
 * Invites every team member (Members API; the response carries the one-time token), signs the member up through
 * Supabase Auth (confirmation via Mailpit, or a plain sign-in when the user already exists) and accepts the
 * invitation with the member's own token. Returns role → member.
 */
export async function addTeam(cfg, ownerApi, team, tenantId, log = () => {}) {
  const members = {};
  for (const m of team) {
    const issued = await ownerApi.post("/api/v1/members/invitations", { email: m.email, role: m.role });
    const userApi = new Api(cfg, await ensureUser(cfg, m));
    await userApi.post("/api/v1/invitations/accept", { token: issued.token });
    const self = await userApi.get("/api/v1/me");
    members[m.role] = { userId: self.userId, email: m.email, fullName: m.fullName, role: m.role, api: userApi.withTenant(tenantId) };
  }
  if (team.length) log(`  team: ${team.map((m) => `${m.role} ${m.email}`).join(", ")}`);
  return members;
}

const isoDateInDays = (days) => new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10);

/** Main-warehouse shelf of a variant (its category shelf), or null in a warehouse without locations. */
function shelfOf(refs, sku) {
  const main = refs.warehouses[refs.def.warehouses[0].code];
  if (!main.usesLocations) return null;
  return main.locations[refs.variants[sku].category] ?? Object.values(main.locations)[0];
}

/** Stock documents only use products no order needs, and never more than was received. */
function assertFreeStock(refs, sku, quantity, what) {
  if (!refs.variants[sku]) throw new Error(`${what}: SKU ${sku} is not in the demo catalog`);
  if (refs.demand[sku]) throw new Error(`${what}: ${sku} is also ordered; pick a product without orders`);
  if (quantity > (refs.stock[sku]?.main ?? 0)) throw new Error(`${what}: ${sku} ${quantity} > ${refs.stock[sku]?.main ?? 0} received`);
}

/** Shelf-to-shelf moves in the main warehouse (#168): category shelf → the overflow shelf. */
export async function seedRelocations(refs) {
  const { api, def, variants, warehouses } = refs;
  if (!def.relocations?.length) return [];
  const main = warehouses[def.warehouses[0].code];
  const to = main.locations.overflow;
  if (!to) throw new Error("Shelf moves need an overflow shelf (key: overflow) in the main warehouse");
  const done = [];
  for (const [sku, quantity] of def.relocations) assertFreeStock(refs, sku, quantity, "Shelf move");
  await api.post("/api/v1/inventory/relocations", {
    warehouseId: main.id,
    reference: "Taşma rafına aktarım (sentetik)",
    lines: def.relocations.map(([sku, quantity]) => {
      done.push({ sku, quantity, from: shelfOf(refs, sku).code, to: to.code });
      return { variantId: variants[sku].id, fromLocationId: shelfOf(refs, sku).id, toLocationId: to.id, quantity, includeReserved: false };
    }),
  });
  return done;
}

/**
 * Two-stage transfers main → second warehouse (OD-01): create → dispatch ("Yolda"); optionally receive (per line, in
 * line order) and close the rest with a resolution (lost / return_to_source) → "Farkla kapatıldı".
 */
export async function seedTransfers(refs) {
  const { api, def, variants, warehouses } = refs;
  const out = [];
  if (!def.transfers?.length) return out;
  const [source, destination] = def.warehouses.map((w) => warehouses[w.code]);
  const committed = {};
  for (const [sku, quantity] of [...(def.relocations ?? []), ...def.transfers.flatMap((t) => t.lines)]) committed[sku] = (committed[sku] ?? 0) + quantity;
  for (const [sku, quantity] of Object.entries(committed)) assertFreeStock(refs, sku, quantity, "Transfer");
  for (const t of def.transfers) {
    let detail = await api.post("/api/v1/stock-transfers", {
      sourceWarehouseId: source.id,
      destinationWarehouseId: destination.id,
      note: t.note,
      lines: t.lines.map(([sku, quantity]) => ({ variantId: variants[sku].id, quantity, sourceLocationId: shelfOf(refs, sku)?.id ?? null, destinationLocationId: null })),
    });
    const act = async (action, body) => {
      detail = (await api.post(`/api/v1/stock-transfers/${detail.transfer.id}/${action}`, body, { ifMatch: etagOf(detail.transfer.version) })).transfer;
    };
    await act("dispatch");
    if (t.receive) {
      const lines = [...detail.lines].sort((a, b) => a.lineNo - b.lineNo);
      const received = lines
        .map((l, i) => ({ lineId: l.id, variantId: null, code: null, quantity: t.receive[i] ?? 0, locationId: null, locationCode: null }))
        .filter((l) => l.quantity > 0);
      await act("receive", { lines: received });
    }
    if (t.close) await act("close", { resolution: t.close.resolution, reason: t.close.reason });
    if (detail.transfer.status !== t.target) throw new Error(`Transfer ${detail.transfer.number} is ${detail.transfer.status}, expected ${t.target}`);
    out.push({ id: detail.transfer.id, number: detail.transfer.number, status: detail.transfer.status, hasDiscrepancy: detail.transfer.hasDiscrepancy });
  }
  return out;
}

/** One open count on a main-warehouse shelf: the first lines counted (deltas against the system quantity), rest pending. */
export async function seedStockCount(refs) {
  const { api, def, warehouses } = refs;
  const spec = def.stockCount;
  if (!spec) return null;
  const main = warehouses[def.warehouses[0].code];
  const shelf = main.locations[spec.shelf];
  if (!shelf) throw new Error(`Stock count shelf ${spec.shelf} not found in ${def.warehouses[0].code}`);
  let count = await api.post("/api/v1/stock-counts", { warehouseId: main.id, scope: "locations", locationIds: [shelf.id], variantIds: null, blind: false, note: spec.note });
  const lines = count.lines.slice(0, spec.counted.length);
  if (lines.length < spec.counted.length) throw new Error(`Stock count on ${shelf.code} has only ${count.lines.length} lines`);
  count = await api.post(
    `/api/v1/stock-counts/${count.count.id}/entries`,
    {
      entries: lines.map((l, i) => ({
        lineId: l.id,
        variantId: null,
        code: null,
        locationId: null,
        locationCode: null,
        quantity: Math.max(0, Number(l.expectedQuantity) + spec.counted[i]),
        mode: "set",
      })),
    },
    { ifMatch: etagOf(count.count.version) },
  );
  return { id: count.count.id, number: count.count.number, status: count.count.status, shelf: shelf.code, lines: count.lines.length, counted: count.count.countedLineCount };
}

/** Units without fractions in the templates and demos (thresholds are whole numbers there). */
const WHOLE_UNITS = new Set(["adet", "takim", "kutu", "paket", "koli", "set", "cift", "rulo"]);

/**
 * Minimum stock thresholds from the stock as it stands after the scripted orders (deterministic: same seed → same
 * balances; candidates in SKU order), via POST /inventory/thresholds/bulk:
 * - 4 variants critical across all warehouses (available ≤ minimum), the second one exactly equal; the demo's
 *   low-stock items come first;
 * - one of them (a variant that also sits in the second warehouse) gets a per-warehouse override there that is below
 *   its stock in that warehouse → critical overall and in the main warehouse, NOT critical in the second one;
 * - 2 variants with a minimum well below their stock (configured, not critical);
 * - `def.outOfStock`: no threshold and 0 stock (shown only with "stokta olmayanlar dahil").
 */
export async function seedThresholds(refs) {
  const { api, def, variants, warehouses } = refs;
  const second = def.warehouses[1];
  const secondId = warehouses[second.code].id;
  const total = new Map();
  const inSecond = new Map();
  for (const b of await api.all("/api/v1/inventory/balances")) {
    total.set(b.variantId, (total.get(b.variantId) ?? 0) + Number(b.available));
    if (b.warehouseId === secondId) inSecond.set(b.variantId, (inSecond.get(b.variantId) ?? 0) + Number(b.available));
  }
  const candidates = Object.entries(variants)
    .filter(([sku, v]) => WHOLE_UNITS.has(v.unit) && !def.outOfStock?.includes(sku))
    .map(([sku, v]) => ({ sku, id: v.id, available: total.get(v.id) ?? 0, second: inSecond.get(v.id) ?? 0 }))
    .filter((c) => Number.isInteger(c.available) && c.available >= 1)
    .sort((a, b) => (a.sku < b.sku ? -1 : a.sku > b.sku ? 1 : 0));
  const override = candidates.find((c) => Number.isInteger(c.second) && c.second >= 2);
  if (!override) throw new Error(`No variant with stock in ${second.code} for the per-warehouse threshold override`);
  const lowFirst = [...def.lowStock.map((sku) => candidates.find((c) => c.sku === sku)).filter(Boolean), ...candidates];
  const others = [...new Set(lowFirst)].filter((c) => c !== override);
  const critical = [override, ...others.slice(0, 3)].map((c, i) => ({ ...c, min: i === 1 ? c.available : c.available + 3 + i }));
  const fine = others
    .slice(3)
    .filter((c) => c.available >= 10)
    .slice(0, 2)
    .map((c) => ({ ...c, min: Math.floor(c.available / 2) }));
  const overrideMin = override.second - 1;
  const items = [
    ...[...critical, ...fine].map((c) => ({ variantId: c.id, warehouseId: null, minQuantity: c.min, remove: null })),
    { variantId: override.id, warehouseId: secondId, minQuantity: overrideMin, remove: null },
  ];
  await api.post("/api/v1/inventory/thresholds/bulk", { items });
  return {
    critical: critical.map(({ sku, available, min }) => ({ sku, available, min })),
    fine: fine.map(({ sku, available, min }) => ({ sku, available, min })),
    override: { sku: override.sku, warehouse: second.code, warehouseId: secondId, available: override.second, min: overrideMin },
    outOfStock: def.outOfStock ?? [],
  };
}

/** Creates one order and drives it to its target state with the real endpoints. */
export async function driveOrder(refs, spec, index = 0) {
  const { api, variants, customers } = refs;
  const customer = customers[spec.customer];
  let detail = await api.post("/api/v1/orders", {
    customerId: customer.id,
    warehouseId: null,
    source: "manual",
    sourceReferenceId: null,
    currency: null,
    pricesIncludeTax: null,
    requestedDeliveryDate: spec.deliveryInDays == null ? null : isoDateInDays(spec.deliveryInDays),
    notes: spec.notes ?? "Sentetik demo siparişi.",
    lines: spec.lines.map(([sku, quantity]) => {
      if (!variants[sku]) throw new Error(`Order line SKU ${sku} is not in the demo catalog`);
      return { id: null, variantId: variants[sku].id, quantity, unitPrice: null, discountPercent: null, discountAmount: null };
    }),
    // Both chosen explicitly: from submit on the order keeps a snapshot of each (#136).
    shippingAddressId: customer.addressId,
    billingAddressId: customer.billingAddressId,
  });
  const id = detail.order.id;
  const act = async (action, body) => {
    detail = await api.post(`/api/v1/orders/${id}/${action}`, body, { ifMatch: etagOf(detail.order.version) });
  };

  if (spec.target === "cancelled_draft") await act("cancel", { reason: spec.reason });
  if (spec.target !== "draft" && spec.target !== "cancelled_draft") await act("submit", { confirmNegativeStock: false });
  if (["approved", "picking", "packed", "partial_closed", "shipped", "completed", "cancelled"].includes(spec.target)) await act("approve", { confirmNegativeStock: false });
  if (spec.target === "cancelled") await act("cancel", { reason: spec.reason });

  // Approved and waiting: the supervisor sets the task's priority (WHT-03) and may assign it to a team member.
  if (spec.target === "approved" && (spec.priority || spec.assignTo)) {
    const { summary, assignee } = await prepareTask(refs, id, spec);
    if (assignee) await api.post(`/api/v1/picking-tasks/${summary.id}/assign`, { userId: assignee });
  }

  if (["picking", "packed", "partial_closed", "shipped", "completed"].includes(spec.target)) {
    const task = await startPickTask(refs, id, spec);
    if (spec.target === "picking") {
      const first = task.lines[0];
      await api.post(`/api/v1/picking-tasks/${task.task.id}/scans`, { code: first.barcode ?? first.sku, location: first.locationCode, quantity: Math.max(1, Math.floor(Number(first.quantityToPick) / 2)), lineId: first.id });
    } else {
      const shortage = spec.shortage ? detail.lines[spec.shortage.line] : null;
      for (const line of task.lines) {
        const short = shortage && line.orderLineId === shortage.id ? spec.shortage.quantity : 0;
        const toPick = Number(line.quantityToPick) - short;
        if (toPick > 0) await api.post(`/api/v1/picking-tasks/${task.task.id}/scans`, { code: line.barcode ?? line.sku, location: line.locationCode, quantity: toPick, lineId: line.id });
        if (short > 0) await api.post(`/api/v1/picking-tasks/${task.task.id}/lines/${line.id}/shortage`, { quantity: short, reason: spec.shortage.reason });
      }
      const completed = await api.post(`/api/v1/picking-tasks/${task.task.id}/complete`);
      const shipment = await api.get(`/api/v1/shipments/${completed.shipmentId}`);
      await api.post(`/api/v1/shipments/${shipment.id}/pack`, {
        packages: [{ items: shipment.lines.filter((l) => Number(l.quantity) > 0).map((l) => ({ code: null, shipmentLineId: l.id, quantity: l.quantity })) }],
      });
      if (spec.target !== "packed") {
        await api.post(`/api/v1/shipments/${shipment.id}/confirm`, { carrier: "Demo Kargo (sentetik)", trackingNumber: `DEMO-${refs.def.code.toUpperCase()}-${String(index + 1).padStart(4, "0")}`, packageCount: 1 });
      }
      detail = await api.get(`/api/v1/orders/${id}`);
      if (spec.target === "partial_closed") await act("close-remaining", { reason: "Eksik kalan miktar kapatıldı (sentetik)" });
      if (spec.target === "completed") await act("complete");
    }
    detail = await api.get(`/api/v1/orders/${id}`);
  }
  return { target: spec.target, id, number: detail.order.number, status: detail.order.status, fulfillmentStatus: detail.order.fulfillmentStatus };
}

/** The order's active pick task (approval creates it in the background). */
export async function findPickTask(refs, orderId) {
  let summary = null;
  for (let i = 0; i < 60 && !summary; i++) {
    summary = (await refs.api.get(`/api/v1/picking-tasks?orderId=${orderId}`)).items.find((t) => t.type === "pick" && ["open", "in_progress"].includes(t.status)) ?? null;
    if (!summary) await sleep(500);
  }
  if (!summary) throw new Error(`No pick task appeared for order ${orderId}`);
  return summary;
}

/** Finds the task, sets the spec's priority (low/normal/high/urgent) and resolves spec.assignTo (a team role) to a user id. */
async function prepareTask(refs, orderId, spec = {}) {
  const summary = await findPickTask(refs, orderId);
  if (spec.priority && spec.priority !== summary.priority) await refs.api.post(`/api/v1/picking-tasks/${summary.id}/priority`, { priority: spec.priority });
  return { summary, assignee: (spec.assignTo && refs.members?.[spec.assignTo]?.userId) || null };
}

/**
 * Approval creates the pick task; the owner (a supervisor, who may act on any task) assigns it — to the spec's team
 * member when there is one, else to himself — and starts it.
 */
export async function startPickTask(refs, orderId, spec = {}) {
  const { summary, assignee } = await prepareTask(refs, orderId, spec);
  await refs.api.post(`/api/v1/picking-tasks/${summary.id}/assign`, { userId: assignee ?? refs.userId });
  await refs.api.post(`/api/v1/picking-tasks/${summary.id}/start`);
  return refs.api.get(`/api/v1/picking-tasks/${summary.id}`);
}

/** Parses the demo's WhatsApp message (rules parser) and confirms the lines a reviewer would confirm. */
export async function whatsappDraft(refs) {
  const { api, def, variants, customers } = refs;
  const customer = customers[def.whatsapp.customer];
  // AI parsing is opt-in per tenant (data-policy §7, off by default); the parse runs in a background job (202).
  await api.put("/api/v1/ai/settings", { aiParsingEnabled: true });
  const accepted = await api.post("/api/v1/ai/order-parses", { text: def.whatsapp.text, customerId: customer.id });
  let status = { state: "pending" };
  for (let i = 0; i < 120 && status.state === "pending"; i++) {
    await sleep(500);
    status = await api.get(accepted.statusUrl);
  }
  if (status.state !== "succeeded") throw new Error(`The WhatsApp parse did not finish: ${status.state} ${status.failureCode ?? ""}`);
  const parse = await api.get(accepted.resultUrl);
  const lines = parse.lines.map((line) => {
    const intent = def.whatsapp.lines.find((i) => line.rawText.toLowerCase().includes(i.match));
    return intent && variants[intent.sku]
      ? { lineNo: line.lineNo, include: true, variantId: variants[intent.sku].id, quantity: intent.quantity }
      : { lineNo: line.lineNo, include: false, variantId: null, quantity: null };
  });
  if (!lines.some((l) => l.include)) throw new Error(`The parser found none of the expected lines: ${JSON.stringify(parse.lines.map((l) => l.rawText))}`);
  const approved = await api.post(`/api/v1/ai/order-parses/${parse.id}/approve`, { customerId: customer.id, warehouseId: null, notes: "WhatsApp mesajından (sentetik).", lines });
  const order = await api.get(`/api/v1/orders/${approved.orderId}`);
  return { parseId: parse.id, parser: parse.parser, summary: parse.summary, orderId: approved.orderId, orderNumber: order.order.number, status: order.order.status };
}
