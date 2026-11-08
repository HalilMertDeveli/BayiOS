import { randomBytes } from "node:crypto";
import { expect, test } from "@playwright/test";
import { Api, ensureUser, stackConfig } from "./demo/stack.mjs";
import { createDraftOrder, expectStock, signIn, submitAndApprove } from "./support/demo";
import { STATE_FILE, writeState, type FlowProduct, type FlowState, type FlowUser } from "./support/mobile-flow";
import { logApiErrors } from "./support/real-stack";

/**
 * WEB ↔ MOBILE REAL FLOW (#45), phase 1 of 3 — real stack, no mocks (support/mobile-flow.ts explains the phases).
 *
 * A fresh, synthetic business per run, seeded only through the public API as its owner (like the demo seeder): sector
 * template, a warehouse with three shelves, products A/B/C with barcodes, goods receipts for A and B, a default price
 * list and a customer. A warehouse_staff colleague is invited and accepts with his own Supabase account. Then the owner
 * creates the order (A × 3, B × 2) in the web UI, submits and approves it; the web stock screen shows the reservation.
 * Writes the state + the Dart define file for the mobile phase. Not part of test:e2e:real: run by apps/mobile/tool/e2e.sh.
 */
const ORDER = { A: 3, B: 2 } as const;
const INITIAL = { A: 10, B: 6, C: 0 } as const;
const RECEIVED_ON_MOBILE = 4;

test("web↔mobil 1/3: işletme + depo personeli + ürünler (API), sipariş web'de oluşturulur ve onaylanır", async ({ page }) => {
  test.setTimeout(240_000);
  logApiErrors(page);
  const cfg = stackConfig();
  const stamp = `${Date.now().toString(36)}${randomBytes(2).toString("hex")}`;
  const digits = `${Date.now()}`.slice(-8);
  const user = (role: string, fullName: string): FlowUser => ({
    email: `e2e-mobile-${role}-${stamp}@example.test`,
    password: `E2e-Mobil-${randomBytes(12).toString("base64url")}-9`,
    fullName,
  });
  const owner = user("owner", "E2E Mobil Sahip");
  const staff = user("staff", "E2E Mobil Depocu");
  const tenantName = `E2E Mobil Akış ${stamp}`;

  // ---- Business (owner's own Supabase token; same calls as the onboarding / demo seeder).
  const ownerApi = new Api(cfg, await ensureUser(cfg, owner));
  const tenant = await ownerApi.post<{ id: string }>("/api/v1/tenants", { name: tenantName, slug: `e2e-mobil-${stamp}`, sector: "motorcycle_parts" });
  const api = ownerApi.withTenant(tenant.id);
  await api.post("/api/v1/sector-templates/motorcycle_parts/apply");
  await api.patch("/api/v1/tenant/settings", { negativeStockPolicy: "block", defaultTaxRate: 20, pricesIncludeTaxDisplay: false });

  const warehouse = await api.post<{ id: string }>("/api/v1/warehouses", { code: `MOB${digits.slice(-4)}`, name: "Mobil Akış Depo", usesLocations: true });
  const shelves = { A: "A-01-01", B: "B-02-01", C: "C-03-01" } as const;
  const locationIds: Record<string, string> = {};
  for (const [index, [key, code]] of Object.entries(shelves).entries()) {
    locationIds[key] = (await api.post<{ id: string }>(`/api/v1/warehouses/${warehouse.id}/locations`, { code, barcode: null, pickSequence: (index + 1) * 10 })).id;
  }
  // The new warehouse is the one orders reserve from (the template may have created none or another).
  const warehouses = (await api.get<{ items: { id: string; isDefault: boolean }[] }>("/api/v1/warehouses")).items;
  if (!warehouses.find((w) => w.id === warehouse.id)?.isDefault) {
    await api.patch(`/api/v1/warehouses/${warehouse.id}`, { isDefault: true });
  }

  const units = (await api.get<{ items: { id: string; code: string }[] }>("/api/v1/units")).items;
  const piece = units.find((u) => u.code === "adet")?.id;
  expect(piece, "motorcycle template provides the 'adet' unit").toBeTruthy();
  const names = { A: "E2E Fren Balatası Ön", B: "E2E Zincir Seti 428", C: "E2E Hava Filtresi" } as const;
  const products = {} as Record<"A" | "B" | "C", FlowProduct>;
  for (const [i, key] of (["A", "B", "C"] as const).entries()) {
    const sku = `MOB-${key}-${digits}`;
    const barcode = `86${digits}${i}1`;
    const product = await api.post<{ id: string; variants: { id: string; sku: string }[] }>("/api/v1/products", {
      name: `${names[key]} ${stamp}`,
      description: "Sentetik E2E ürünü — gerçek bir ürün değildir.",
      categoryId: null,
      brandId: null,
      defaultUnitId: piece,
      taxRate: 20,
      status: null,
      attributes: null,
      sku,
      barcode,
      variants: null,
      variantAttributes: null,
    });
    products[key] = {
      sku,
      barcode,
      name: `${names[key]} ${stamp}`,
      variantId: product.variants[0].id,
      shelf: shelves[key],
      locationId: locationIds[key],
      initial: INITIAL[key],
      ordered: key === "C" ? 0 : ORDER[key],
    };
  }
  await api.post("/api/v1/inventory/receipts", {
    warehouseId: warehouse.id,
    reference: `IRS-E2E-MOB-${digits}`,
    lines: (["A", "B"] as const).map((k) => ({ variantId: products[k].variantId, locationId: products[k].locationId, quantity: INITIAL[k], unitCost: 50 })),
  });
  const list = await api.post<{ id: string }>("/api/v1/price-lists", {
    name: `E2E Mobil Perakende ${stamp}`,
    kind: "sales",
    currency: "TRY",
    isDefault: true,
    pricesIncludeTax: false,
    validFrom: null,
    validTo: null,
    basePriceListId: null,
    discountPercent: null,
  });
  await api.put(`/api/v1/price-lists/${list.id}/items`, {
    items: (["A", "B", "C"] as const).map((k, i) => ({ variantId: products[k].variantId, minQuantity: 1, unitPrice: 100 + i * 25 })),
  });
  const customer = { code: `M-${digits}`, name: `E2E Mobil Bayi ${stamp}` };
  await api.post("/api/v1/customers", {
    code: customer.code,
    name: customer.name,
    contactName: null,
    taxNumber: null,
    taxOffice: null,
    phone: null,
    email: null,
    whatsappNumbers: null,
    dealerGroupId: null,
    priceListId: null,
    paymentTermDays: 30,
    creditLimit: null,
    currency: null,
    notes: "Sentetik E2E müşterisi.",
  });

  // ---- Warehouse staff: invitation (owner) → own Supabase account (Mailpit confirmation) → accept.
  const invitation = await api.post<{ token: string }>("/api/v1/members/invitations", { email: staff.email, role: "warehouse_staff" });
  const staffApi = new Api(cfg, await ensureUser(cfg, staff));
  const accepted = await staffApi.post<{ tenantId: string; role: string }>("/api/v1/invitations/accept", { token: invitation.token });
  expect(accepted).toMatchObject({ tenantId: tenant.id, role: "warehouse_staff" });

  // ---- Web: the owner creates the order by scanning barcodes, submits and approves it (reservation).
  await signIn(page, owner.email, owner.password, owner.fullName);
  const { orderId, orderNumber } = await createDraftOrder(page, customer, [
    { sku: products.A.sku, code: products.A.barcode, quantity: ORDER.A },
    { sku: products.B.sku, code: products.B.barcode, quantity: ORDER.B },
  ]);
  await submitAndApprove(page, orderNumber);
  await expect(page.getByRole("list", { name: "Durum geçmişi" })).toContainText("Onaylandı, stok rezerve edildi");
  await expectStock(page, warehouse.id, products.A.sku, INITIAL.A, ORDER.A, INITIAL.A - ORDER.A);
  await expectStock(page, warehouse.id, products.B.sku, INITIAL.B, ORDER.B, INITIAL.B - ORDER.B);

  // Approval created exactly one pick task, in the pool (nobody assigned) — the staff user claims it on mobile.
  await expect
    .poll(async () => {
      const tasks = (await api.get<{ items: { type: string; status: string; assignedTo: string | null }[] }>(`/api/v1/picking-tasks?orderId=${orderId}`)).items;
      return tasks.map((t) => [t.type, t.status, t.assignedTo]);
    })
    .toEqual([["pick", "open", null]]);

  const state: FlowState = {
    stamp,
    tenantId: tenant.id,
    tenantName,
    warehouseId: warehouse.id,
    owner,
    staff,
    products,
    received: RECEIVED_ON_MOBILE,
    orderId,
    orderNumber,
  };
  writeState(state);
  console.log(`[flow] order ${orderNumber} approved in tenant ${tenant.id}; state → ${STATE_FILE}`);
});
