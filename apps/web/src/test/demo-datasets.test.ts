// @vitest-environment node
import { describe, expect, it } from "vitest";
import { GENEL_CATEGORIES, PRIORITIES, genelDemo } from "../../e2e-local/demo/data-genel.mjs";
import { ledDemo } from "../../e2e-local/demo/data-led.mjs";
import { motorDemo } from "../../e2e-local/demo/data-motor.mjs";

/**
 * The demo datasets are pure functions (apps/web/e2e-local/demo/data-*.mjs): these checks run without a stack. The
 * seeder itself runs against the real local stack in CI (`demo seed (twice)`) and in the `demo-*.spec.ts` E2E.
 */

type Def = ReturnType<typeof genelDemo>;
const sizes = ["full", "small"] as const;

/** JSON without the stock function (it draws from the seeded RNG). */
const data = (def: Def) => JSON.stringify({ ...def, stock: undefined });

const allSkus = (def: Def) => [...def.products.flatMap((p) => p.variants.map((v) => v.sku)), ...def.importProducts.map((p) => p.sku)];
const allBarcodes = (def: Def) => [...def.products.flatMap((p) => p.variants.map((v) => v.barcode)), ...def.importProducts.map((p) => p.barcode)];

function ean13Valid(code: string) {
  if (!/^\d{13}$/.test(code)) return false;
  let sum = 0;
  for (let i = 0; i < 12; i++) sum += Number(code[i]) * (i % 2 === 0 ? 1 : 3);
  return (10 - (sum % 10)) % 10 === Number(code[12]);
}

/** Every SKU the order / WhatsApp lines ask for. */
function demandOf(def: Def) {
  const demand: Record<string, number> = {};
  for (const o of def.orders) for (const [sku, q] of o.lines) demand[sku] = (demand[sku] ?? 0) + q;
  for (const l of def.whatsapp.lines) demand[l.sku] = (demand[l.sku] ?? 0) + l.quantity;
  return demand;
}

describe("generic wholesale demo (genel) — the default demo", () => {
  it("is deterministic: same size, same dataset", () => {
    for (const size of sizes) expect(data(genelDemo(size))).toBe(data(genelDemo(size)));
    expect(data(genelDemo("full"))).not.toBe(data(genelDemo("small")));
  });

  it("is a general-template wholesaler with its own units, categories and custom fields", () => {
    const def = genelDemo();
    expect(def).toMatchObject({ code: "genel", sector: "general", tenant: { name: "Demo Toptan Ticaret", slug: "demo-genel" } });
    const units = new Set(["adet", "koli", "kg", ...def.units.map((u) => u.code)]); // general template: adet, koli, kg
    for (const p of [...def.products, ...def.importProducts]) {
      expect(units, p.name).toContain(p.unit);
      expect(GENEL_CATEGORIES, p.name).toContain(p.category);
    }
    expect(def.customFields.map((f) => f.key)).toEqual(["koli_ici", "mensei"]);
    const mensei = def.customFields.find((f) => f.key === "mensei")!;
    for (const p of [...def.products, ...def.importProducts]) if (p.attributes.mensei) expect(mensei.options).toContain(p.attributes.mensei);
  });

  it("has 60–120 products with variants and unique SKUs and synthetic EAN-13 barcodes (GS1 in-store range 202)", () => {
    for (const size of sizes) {
      const def = genelDemo(size);
      const skus = allSkus(def);
      expect(new Set(skus).size, `${size}: unique SKUs`).toBe(skus.length);
      const barcodes = allBarcodes(def);
      expect(new Set(barcodes).size).toBe(barcodes.length);
      for (const b of barcodes) {
        expect(b.startsWith("202"), b).toBe(true);
        expect(ean13Valid(b), b).toBe(true);
      }
    }
    const full = genelDemo("full");
    const products = full.products.length + full.importProducts.length;
    expect(products).toBeGreaterThanOrEqual(60);
    expect(products).toBeLessThanOrEqual(120);
    expect(full.products.filter((p) => p.variants.length > 1).length).toBeGreaterThanOrEqual(20);
    expect(full.importProducts.length).toBeGreaterThan(0);
  });

  it("has two warehouses: a shelved main warehouse (A-01…, overflow shelf) and a branch for transfers", () => {
    const [main, branch, ...rest] = genelDemo().warehouses;
    expect(rest).toHaveLength(0);
    expect(main).toMatchObject({ name: "Merkez Depo", usesLocations: true });
    expect(main.locations[0].code).toBe("A-01");
    expect(main.locations.map((l) => l.category).filter(Boolean)).toEqual(GENEL_CATEGORIES);
    expect(main.locations.some((l) => l.key === "overflow")).toBe(true);
    expect(branch).toMatchObject({ name: "Şube Depo", usesLocations: false });
  });

  it("has ~15 customers in three groups, two of them with their own price list", () => {
    const def = genelDemo();
    expect(def.customers).toHaveLength(15);
    expect(new Set(def.customers.map((c) => c.code)).size).toBe(15);
    const groups = new Set(def.customers.map((c) => c.group));
    expect([...groups].sort()).toEqual([...def.dealerGroups].sort());
    expect(def.priceLists.dealer.length).toBeGreaterThanOrEqual(2);
    for (const list of def.priceLists.dealer) expect(groups).toContain(list.group);
    expect(def.products.some((p) => p.tiers?.length)).toBe(true); // quantity tiers on the default list
  });

  it("references only catalog SKUs and customers (orders, WhatsApp, transfers, shelf moves, tiers, thresholds)", () => {
    for (const size of sizes) {
      const def = genelDemo(size);
      const skus = new Set(allSkus(def));
      const referenced = [
        ...def.orders.flatMap((o) => o.lines.map(([sku]) => sku)),
        ...def.whatsapp.lines.map((l) => l.sku),
        ...def.transfers.flatMap((t) => t.lines.map(([sku]) => sku)),
        ...def.relocations.map(([sku]) => sku),
        ...def.lowStock,
        ...def.outOfStock,
      ];
      for (const sku of referenced) expect(skus, `${size}: ${sku}`).toContain(sku);
      for (const o of def.orders) expect(def.customers[o.customer], `${size}: customer ${o.customer}`).toBeDefined();
      expect(def.customers[def.whatsapp.customer]).toBeDefined();
      for (const l of def.whatsapp.lines) expect(def.whatsapp.text.toLowerCase()).toContain(l.match);
    }
  });

  it("has orders in every state, with prioritised picking tasks incl. an urgent one assigned to warehouse staff", () => {
    for (const size of sizes) {
      const def = genelDemo(size);
      const targets = new Set(def.orders.map((o) => o.target));
      for (const t of ["draft", "approved", "picking", "shipped"]) expect(targets, `${size}: ${t}`).toContain(t);
      for (const o of def.orders) if (o.priority) expect(PRIORITIES).toContain(o.priority);
      const urgent = def.orders.find((o) => o.priority === "urgent");
      expect(urgent).toMatchObject({ target: "approved", assignTo: "warehouse_staff" });
      expect(def.orders.find((o) => o.target === "picking")?.assignTo).toBe("warehouse_staff");
      const roles = new Set(def.team.map((m) => m.role));
      for (const o of def.orders) if (o.assignTo) expect(roles).toContain(o.assignTo);
    }
    const full = genelDemo("full");
    const targets = new Set(full.orders.map((o) => o.target));
    for (const t of ["pending", "packed", "partial_closed", "completed", "cancelled", "cancelled_draft"]) expect(targets).toContain(t);
    expect(new Set(full.orders.map((o) => o.priority).filter(Boolean))).toEqual(new Set(PRIORITIES));
  });

  it("has a transfer on the road and one received short, a shelf move and an open stock count", () => {
    for (const size of sizes) {
      const def = genelDemo(size);
      expect(def.transfers.some((t) => t.target === "in_transit" && !t.receive)).toBe(true);
      const short = def.transfers.find((t) => t.close);
      expect(short).toBeDefined();
      const sent = short!.lines.reduce((n, [, q]) => n + q, 0);
      const received = short!.receive!.reduce((n: number, q: number) => n + q, 0);
      expect(received).toBeLessThan(sent);
      expect(["lost", "return_to_source"]).toContain(short!.close!.resolution);
      for (const t of def.transfers) if (t.receive) expect(t.receive).toHaveLength(t.lines.length);
      expect(def.relocations.length).toBeGreaterThan(0);
      expect(GENEL_CATEGORIES).toContain(def.stockCount.shelf);
      expect(def.stockCount.counted.some((d) => d !== 0)).toBe(true); // a difference to review
    }
  });

  it("moves only stock no order needs, never more than the smallest goods receipt", () => {
    for (const size of sizes) {
      const def = genelDemo(size);
      const demand = demandOf(def);
      const committed: Record<string, number> = {};
      for (const [sku, q] of [...def.relocations, ...def.transfers.flatMap((t) => t.lines)]) committed[sku] = (committed[sku] ?? 0) + q;
      for (const [sku, q] of Object.entries(committed)) {
        expect(demand[sku], `${size}: ${sku} is ordered`).toBeUndefined();
        expect(def.lowStock, sku).not.toContain(sku);
        expect(def.outOfStock, sku).not.toContain(sku);
        expect(q, sku).toBeLessThanOrEqual(18); // stock().main is at least 18
      }
      for (const sku of def.outOfStock) expect(demand[sku], `${size}: ${sku} is out of stock by design`).toBeUndefined();
      // The goods-receipt quantities: whole numbers, main ≥ 18, the branch stocks some categories (threshold override).
      const draws = def.products.map((p) => def.stock(null, p.category));
      expect(draws.every((s) => Number.isInteger(s.main) && s.main >= 18 && Number.isInteger(s.shop))).toBe(true);
      expect(draws.some((s) => s.shop >= 2)).toBe(true);
    }
  });

  it("has a team of owner, manager, sales and warehouse staff — synthetic addresses only", () => {
    const def = genelDemo();
    expect(def.team.map((m) => m.role)).toEqual(["manager", "sales", "warehouse_staff"]);
    for (const email of [def.owner.email, ...def.team.map((m) => m.email)]) expect(email).toMatch(/@demo-genel\.example\.test$/);
    for (const c of def.customers) {
      expect(c.email).toMatch(/@demo-genel\.example\.com$/);
      expect(c.phone).toMatch(/^\+90 000 000 /);
      expect(c.name).toMatch(/\(sentetik\)$/);
    }
    for (const p of [...def.products, ...def.importProducts]) expect(p.name).toMatch(/\(sentetik\)$/);
  });
});

describe("demo datasets — no collisions between demos", () => {
  it("uses distinct tenants, owners and barcodes", () => {
    const defs = [genelDemo(), motorDemo(), ledDemo()] as unknown as Def[];
    expect(new Set(defs.map((d) => d.tenant.slug)).size).toBe(3);
    expect(new Set(defs.map((d) => d.owner.email)).size).toBe(3);
    const barcodes = defs.flatMap(allBarcodes);
    expect(new Set(barcodes).size).toBe(barcodes.length);
  });
});
