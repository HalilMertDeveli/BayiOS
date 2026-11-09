/**
 * Minimum stock / critical stock (#137): product editor "Minimum stok", per-warehouse overrides on the variant stock
 * page, the "Min. stok" column + "Kritik" badge in /stok, the dashboard tile and the critical-stock report default.
 * MOCK: Supabase Auth and the BayiOS API (in-memory Catalog + Inventory incl. thresholds, Reports) are faked with
 * page.route — support/mock-backend.ts, support/mock-inventory.ts, support/mock-reports.ts. All data is synthetic.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog, productId } from "./support/mock-catalog";
import { WAREHOUSE_IDS, createMockInventory } from "./support/mock-inventory";
import { createMockReports } from "./support/mock-reports";

test.use({ viewport: DESKTOP });

const VIEWER: MockMembership = { ...TENANT_A, role: "viewer" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

function setup() {
  const catalog = createMockCatalog({ products: 3 });
  const inventory = createMockInventory(catalog, { stocked: 3 });
  return { catalog, inventory };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const bulkCalls = (api: Api) => api.requests.filter((r) => r.method === "POST" && r.path === "/api/v1/inventory/thresholds/bulk");

test("ürün düzenleme: varyantın genel minimum stoku girilir ve tek toplu istekle kaydedilir (MOCK)", async ({ page }) => {
  const { catalog, inventory } = setup();
  const api = await signedIn(page, { catalog, inventory });
  await page.goto(`/urunler/${productId(1)}`);
  const section = page.getByRole("region", { name: "Minimum stok" });
  const input = section.getByRole("textbox", { name: "MOCK-001-1 minimum stok" });
  await expect(input).toBeEnabled();
  await expect(input).toHaveValue("");

  await input.fill("2,5");
  await section.getByRole("button", { name: "Minimum stoku kaydet" }).click();
  await expect(section.getByText(/kesirli olamaz/)).toBeVisible(); // ADET: whole numbers
  expect(bulkCalls(api)).toHaveLength(0);

  await input.fill("12");
  await section.getByRole("button", { name: "Minimum stoku kaydet" }).click();
  await expect(page.getByText("Minimum stok kaydedildi")).toBeVisible();
  const [bulk] = bulkCalls(api);
  expect(bulk.body).toEqual({ items: [{ variantId: catalog.products[0].variants[0].id, warehouseId: null, minQuantity: "12", remove: null }] });
  expect(bulk.headers["idempotency-key"]).toBeTruthy();
  expect(inventory.thresholds).toHaveLength(1);
  await expect(input).toHaveValue("12");
  await expectNoSeriousViolations(page);
});

test("stok listesi: 'Min. stok' sütunu ve depodaki toplam minimumun altındaysa 'Kritik' rozeti (MOCK)", async ({ page }) => {
  const { catalog, inventory } = setup();
  const [v1, v2] = [catalog.products[0].variants[0].id, catalog.products[1].variants[0].id];
  // Balance 1: available 10, balance 2: available 10 (12 − 2) — minimum 10 → kritik, 9 → değil.
  inventory.thresholds.push(
    { id: "00000000-0000-4000-8000-9d0000000001", variantId: v1, warehouseId: null, minQuantity: 10, version: 1, createdAt: "2026-11-07T10:00:00Z", updatedAt: null },
    { id: "00000000-0000-4000-8000-9d0000000002", variantId: v2, warehouseId: WAREHOUSE_IDS.main, minQuantity: 9, version: 1, createdAt: "2026-11-07T10:00:00Z", updatedAt: null },
  );
  await signedIn(page, { catalog, inventory });
  await page.goto("/stok");
  const table = page.getByRole("table", { name: "Stok bakiyeleri" });
  await expect(table.getByRole("columnheader", { name: "Min. stok" })).toBeVisible();
  const row1 = table.getByRole("row").filter({ hasText: "MOCK-001-1" });
  const row2 = table.getByRole("row").filter({ hasText: "MOCK-002-1" });
  const row3 = table.getByRole("row").filter({ hasText: "MOCK-003-1" });
  await expect(row1.getByText("Kritik")).toBeVisible();
  await expect(row2).toContainText("9");
  await expect(row2.getByText("Kritik")).toHaveCount(0);
  await expect(row3.getByText("—").first()).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.setViewportSize(MOBILE);
  const card = page.getByRole("list", { name: "Stok bakiyeleri" }).getByRole("listitem").filter({ hasText: "MOCK-001-1" });
  await expect(card.getByText("Kritik")).toBeVisible();
  await expect(card.getByText(/min\. stok 10/)).toBeVisible();
});

test("varyant stok sayfası: genel eşik + depoya özel istisna kaydedilir; izleyici yalnızca görür (MOCK)", async ({ page }) => {
  const { catalog, inventory } = setup();
  const variant = catalog.products[0].variants[0];
  const api = await signedIn(page, { catalog, inventory });
  await page.goto(`/stok/varyant/${variant.id}`);
  const section = page.getByRole("region", { name: "Minimum stok" });
  await section.getByRole("textbox", { name: "Genel (tüm depolar)" }).fill("5");
  await section.getByRole("textbox", { name: /Raflı Depo \(mock\)/ }).fill("2");
  await section.getByRole("button", { name: "Minimum stoku kaydet" }).click();
  await expect(page.getByText("Minimum stok kaydedildi")).toBeVisible();
  expect(bulkCalls(api)[0].body).toEqual({
    items: [
      { variantId: variant.id, warehouseId: null, minQuantity: "5", remove: null },
      { variantId: variant.id, warehouseId: WAREHOUSE_IDS.raf, minQuantity: "2", remove: null },
    ],
  });
  // Balances reload: MAIN row shows the general minimum (available 10 > 5 → not critical).
  const balances = page.getByRole("table", { name: "Depo ve raf bakiyeleri" });
  await expect(balances.getByRole("row").nth(1)).toContainText("5");

  // Clearing the override removes it.
  await section.getByRole("textbox", { name: /Raflı Depo \(mock\)/ }).fill("");
  await section.getByRole("button", { name: "Minimum stoku kaydet" }).click();
  await expect.poll(() => bulkCalls(api).length).toBe(2);
  expect(bulkCalls(api)[1].body).toEqual({ items: [{ variantId: variant.id, warehouseId: WAREHOUSE_IDS.raf, minQuantity: null, remove: true }] });
  await expectNoSeriousViolations(page);
});

test("varyant stok sayfası: izleyici eşikleri görür, düzenleyemez (MOCK)", async ({ page }) => {
  const { catalog, inventory } = setup();
  const variant = catalog.products[0].variants[0];
  inventory.thresholds.push({ id: "00000000-0000-4000-8000-9d0000000001", variantId: variant.id, warehouseId: null, minQuantity: 4, version: 1, createdAt: "2026-11-07T10:00:00Z", updatedAt: null });
  await signedIn(page, { catalog, inventory, memberships: [VIEWER] });
  await page.goto(`/stok/varyant/${variant.id}`);
  const section = page.getByRole("region", { name: "Minimum stok" });
  await expect(section.getByText("Genel (tüm depolar)")).toBeVisible();
  await expect(section.getByText("4 ADET")).toBeVisible();
  await expect(section.getByRole("textbox")).toHaveCount(0);
  await expect(section.getByRole("button", { name: "Minimum stoku kaydet" })).toHaveCount(0);
});

test("pano: kritik stok kutusu eşik altındaki ürünleri sayar; eşik yoksa nasıl girileceğini anlatır (MOCK)", async ({ page }) => {
  const reports = createMockReports();
  const api = await signedIn(page, { reports });
  await page.goto("/pano");
  const tile = page.getByRole("region", { name: "Kritik stok" });
  await expect(tile.getByText("100+")).toBeVisible();
  await expect(tile.getByText("ürün minimum stokta veya altında")).toBeVisible();
  const read = api.reports!.requests.find((r) => r.method === "GET" && r.path === "/api/v1/reports/critical-stock")!;
  expect(new URLSearchParams(read.search).has("threshold")).toBe(false);

  const none = createMockReports({ empty: new Set(["critical-stock"]), noThresholds: true });
  await signedIn(page, { reports: none });
  await page.goto("/pano");
  await expect(page.getByRole("region", { name: "Kritik stok" }).getByText(/Henüz minimum stok tanımlanmadı/)).toBeVisible();
});

test("kritik stok raporu: varsayılan ürün eşikleri; 'stoğu tükenenler' ve sabit eşik isteğe bağlı (MOCK)", async ({ page }) => {
  const reports = createMockReports();
  const api = await signedIn(page, { reports });
  const reads = () => api.reports!.requests.filter((r) => r.method === "GET" && r.path === "/api/v1/reports/critical-stock");
  await page.goto("/raporlar/kritik-stok");
  const table = page.getByRole("table", { name: "Kritik stok" });
  await expect(table.getByRole("columnheader", { name: "Min. stok" })).toBeVisible();
  await expect(page.getByText(/Her ürün kendi minimum stoğuna göre/)).toBeVisible();
  expect(new URLSearchParams(reads()[0].search).has("threshold")).toBe(false);

  const outOfStock = page.getByRole("checkbox", { name: "Eşiği olmayan, stoğu tükenmiş ürünleri de göster" });
  await outOfStock.click(); // the state follows the URL (tukenen=1)
  await expect(outOfStock).toBeChecked();
  await expect(page).toHaveURL(/tukenen=1/);
  await expect.poll(() => new URLSearchParams(reads().at(-1)?.search).get("includeOutOfStock")).toBe("true");

  await page.getByRole("textbox", { name: "Kritik eşik" }).fill("4");
  await page.getByRole("button", { name: "Uygula" }).click();
  await expect(page).toHaveURL(/esik=4/);
  await expect.poll(() => new URLSearchParams(reads().at(-1)?.search).get("threshold")).toBe("4");
  expect(new URLSearchParams(reads().at(-1)?.search).has("includeOutOfStock")).toBe(false);
  await expect(page.getByRole("checkbox", { name: "Eşiği olmayan, stoğu tükenmiş ürünleri de göster" })).toHaveCount(0);

  // Emptying the threshold returns to each product's minimum.
  await page.getByRole("textbox", { name: "Kritik eşik" }).fill("");
  await page.getByRole("button", { name: "Uygula" }).click();
  await expect(page).not.toHaveURL(/esik=/);
  await expectNoSeriousViolations(page);
});

test("kritik stok raporu: hiç eşik tanımlı değilse ne yapılacağını anlatır (MOCK)", async ({ page }) => {
  await signedIn(page, { reports: createMockReports({ empty: new Set(["critical-stock"]), noThresholds: true }) });
  await page.goto("/raporlar/kritik-stok");
  await expect(page.getByRole("heading", { name: "Henüz minimum stok tanımlanmadı" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Ürünlere git" })).toBeVisible();
});
