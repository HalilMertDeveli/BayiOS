/**
 * Two-stage warehouse transfers (/depo/transferler), stock counts (/depo/sayimlar) and shelf moves (/stok/raf-tasima) — #168 web.
 * MOCK: Supabase Auth and the BayiOS API (incl. in-memory Catalog, Inventory/Warehouse and stock documents) are faked
 * with page.route — support/mock-backend.ts, support/mock-inventory.ts, support/mock-stock-documents.ts. All data is
 * synthetic; nothing here talks to a real API or database.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog } from "./support/mock-catalog";
import { LOCATION_IDS, WAREHOUSE_IDS, createMockInventory } from "./support/mock-inventory";
import { createMockStockDocuments } from "./support/mock-stock-documents";

test.use({ viewport: DESKTOP });

const PHONE = { width: 360, height: 740 } as const;
const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };
const VIEWER: MockMembership = { ...TENANT_A, role: "viewer" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

/** MAIN (no shelves) holds MOCK-001-1 11/1 (on hand / reserved), MOCK-002-1 12/2, MOCK-003-1 13/0; RAF has shelves A-01, A-02. */
function setup() {
  const catalog = createMockCatalog({ products: 3 });
  const inventory = createMockInventory(catalog);
  const stockDocuments = createMockStockDocuments();
  return { catalog, inventory, stockDocuments };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const posts = (api: Api, suffix: string) => api.requests.filter((r) => r.method === "POST" && r.path.endsWith(suffix));
const onHand = (api: Api, sku: string, warehouseId: string, locationId: string | null = null) => {
  const variant = api.catalog!.products.flatMap((p) => p.variants).find((v) => v.sku === sku)!;
  return api.inventory!.balances.find((b) => b.variantId === variant.id && b.warehouseId === warehouseId && b.locationId === locationId)?.onHand ?? 0;
};

/** Exact text that is visible (a previous route can stay mounted but hidden, e.g. the list's "Taslak" filter option). */
const visibleText = (page: Page, text: string) => page.getByText(text, { exact: true }).filter({ visible: true });

async function scanInto(page: Page, label: string, code: string) {
  const box = page.getByRole("textbox", { name: label });
  await box.fill(code);
  await box.press("Enter");
}

test.describe("transfer (mock)", () => {
  test("oluştur → gönder (yolda) → kısmi teslim → farkla kapat; fark raporu ve yolda hareketleri", async ({ page }) => {
    const data = setup();
    const api = await signedIn(page, { memberships: [MANAGER], ...data });
    await page.goto("/depo/transferler");
    await expect(page.getByRole("heading", { name: "Bu filtrede transfer yok" })).toBeVisible();
    // Menu: Transferler and Sayımlar under the warehouse area.
    const nav = page.getByRole("navigation").first();
    await expect(nav.getByRole("link", { name: "Transferler" })).toHaveAttribute("aria-current", "page");
    await expect(nav.getByRole("link", { name: "Sayımlar" })).toBeVisible();
    await page.getByRole("link", { name: "Yeni transfer" }).first().click();
    await expect(page).toHaveURL(/\/depo\/transferler\/yeni$/);

    // Source = the default warehouse; destination must differ (the source is not offered).
    await expect(page.getByRole("combobox", { name: "Kaynak depo" })).toHaveValue(WAREHOUSE_IDS.main);
    const destination = page.getByRole("combobox", { name: "Hedef depo" });
    await expect(destination.locator("option", { hasText: "Ana Depo (mock)" })).toHaveCount(0);
    await destination.selectOption({ label: "Raflı Depo (mock) (RAF)" });

    await scanInto(page, "Barkod veya SKU okutun", "MOCK-001-1");
    await expect(page.getByTestId("transfer-line-MOCK-001-1")).toContainText("Kaynakta satılabilir 10");
    await page.getByRole("textbox", { name: "MOCK-001-1 miktarı" }).fill("12");
    await scanInto(page, "Barkod veya SKU okutun", "MOCK-002-1");
    await page.getByRole("textbox", { name: "MOCK-002-1 miktarı" }).fill("4");
    await page.getByRole("combobox", { name: "MOCK-001-1 planlanan hedef rafı" }).selectOption({ label: "A-01" });
    // More than is available on the source: refused before anything is sent.
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page.getByText("Kaynakta satılabilir yalnızca 10. Miktarı azaltın.")).toBeVisible();
    expect(posts(api, "/api/v1/stock-transfers")).toHaveLength(0);
    await page.getByRole("textbox", { name: "MOCK-001-1 miktarı" }).fill("6");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();

    await expect(page).toHaveURL(/\/depo\/transferler\/[0-9a-f-]{36}$/);
    const created = posts(api, "/api/v1/stock-transfers")[0];
    expect(created.headers["idempotency-key"]).toBeTruthy();
    expect(created.body).toMatchObject({
      sourceWarehouseId: WAREHOUSE_IDS.main,
      destinationWarehouseId: WAREHOUSE_IDS.raf,
      lines: [
        { quantity: 6, sourceLocationId: null, destinationLocationId: LOCATION_IDS.a01 },
        { quantity: 4, sourceLocationId: null, destinationLocationId: null },
      ],
    });
    const transfer = api.stockDocuments!.transfers[0];
    await expect(page.getByRole("heading", { name: `Transfer ${transfer.number}` })).toBeVisible();
    await expect(visibleText(page, "Taslak")).toBeVisible();

    // Dispatch: the stock leaves the source and is "Yolda".
    await page.getByRole("button", { name: "Gönder", exact: true }).click();
    await page.getByRole("alertdialog", { name: /gönderilsin mi/ }).getByRole("button", { name: "Gönder" }).click();
    await expect(visibleText(page, "Yolda").first()).toBeVisible();
    expect(posts(api, "/dispatch")[0].headers["if-match"]).toBe('W/"1"');
    expect(onHand(api, "MOCK-001-1", WAREHOUSE_IDS.main)).toBe(5);
    await expect(page.getByTestId("transfer-totals")).toContainText("Yolda10");

    // Partial receipt per line: 4 of 6 to A-01; the second line not this time.
    await expect(page.getByRole("textbox", { name: "MOCK-001-1 teslim alınan miktar" })).toHaveValue("6");
    await expect(page.getByRole("combobox", { name: "MOCK-001-1 hedef rafı" })).toHaveValue(LOCATION_IDS.a01);
    await page.getByRole("textbox", { name: "MOCK-001-1 teslim alınan miktar" }).fill("4");
    await page.getByRole("textbox", { name: "MOCK-002-1 teslim alınan miktar" }).fill("");
    await page.getByRole("button", { name: "Teslim al", exact: true }).click();
    await expect(visibleText(page, "Kısmen teslim alındı")).toBeVisible();
    await expect(visibleText(page, "Fark var")).toBeVisible();
    expect(posts(api, "/receive")[0].body).toEqual({ lines: [{ lineId: transfer.lines[0].id, variantId: null, code: null, quantity: 4, locationId: LOCATION_IDS.a01, locationCode: null }] });
    expect(onHand(api, "MOCK-001-1", WAREHOUSE_IDS.raf, LOCATION_IDS.a01)).toBe(4);

    // Scan receipt rejections in Turkish: wrong item, unknown shelf, more than in transit.
    const scanForm = page.getByRole("form", { name: "Okutarak teslim al" });
    await scanForm.getByRole("textbox", { name: "Hedef raf kodu" }).fill("A-02");
    await scanForm.getByRole("textbox", { name: "Ürün barkodu veya SKU" }).fill("MOCK-003-1");
    await scanForm.getByRole("button", { name: "Okutulanı teslim al" }).click();
    await expect(scanForm.getByRole("alert")).toHaveText("Okutulan ürün bu transferde yok. Ürünü ayırın ve transferdeki ürünleri okutun.");
    await scanForm.getByRole("textbox", { name: "Hedef raf kodu" }).fill("Z-99");
    await scanForm.getByRole("textbox", { name: "Ürün barkodu veya SKU" }).fill("MOCK-002-1");
    await scanForm.getByRole("button", { name: "Okutulanı teslim al" }).click();
    await expect(scanForm.getByRole("alert")).toContainText("Bu raf kodu hedef depoda yok");
    await scanForm.getByRole("textbox", { name: "Hedef raf kodu" }).fill("A-02");
    await scanForm.getByRole("textbox", { name: "Miktar" }).fill("9");
    await scanForm.getByRole("button", { name: "Okutulanı teslim al" }).click();
    await expect(scanForm.getByRole("alert")).toContainText("Yolda olandan fazlası teslim alınamaz");

    // Close the discrepancy: the rest (2 + 4) is written off as lost.
    await page.getByRole("radio", { name: /Kayıp olarak düş/ }).check();
    await page.getByRole("textbox", { name: "Kapatma sebebi" }).fill("Yolda hasar gördü (test)");
    await page.getByRole("button", { name: "Farkı kapat" }).click();
    await page.getByRole("alertdialog", { name: "Transfer kapatılsın mı?" }).getByRole("button", { name: "Transferi kapat" }).click();
    await expect(visibleText(page, "Farkla kapatıldı")).toBeVisible();
    expect(posts(api, "/close")[0].body).toEqual({ resolution: "lost", reason: "Yolda hasar gördü (test)" });
    const totals = page.getByTestId("transfer-totals");
    await expect(totals).toContainText("Gönderilen10");
    await expect(totals).toContainText("Teslim alınan4");
    await expect(totals).toContainText("Kayıp6");
    await expect(totals).toContainText("Yolda0");
    await expect(page.getByRole("button", { name: "Teslim al", exact: true })).toHaveCount(0);
    // The in-transit legs are named after the transfer, not a (missing) warehouse.
    await expect(page.getByText(`Yolda (Transfer ${transfer.number})`).first()).toBeVisible();
    await expectNoSeriousViolations(page);

    // The list shows the closed transfer with its discrepancy badge.
    await page.goto("/depo/transferler?durum=done");
    const item = page.getByTestId(`transfer-${transfer.number}`);
    await expect(item).toContainText("Farkla kapatıldı");
    await expect(item).toContainText("Fark var");
    await expect(item).toContainText("kayıp 6");

    // Variant stock card: the in-transit movement is "Yolda (Transfer …)".
    const variantId = api.catalog!.products[0].variants[0].id;
    await page.goto(`/stok/varyant/${variantId}`);
    await expect(page.getByRole("table", { name: "Hareket geçmişi" }).getByText(`Yolda (Transfer ${transfer.number})`).first()).toBeVisible();
  });

  test("taslak iptali; gönderimde yetersiz stok satırları Türkçe listelenir", async ({ page }) => {
    const data = setup();
    const api = await signedIn(page, { memberships: [MANAGER], ...data });
    await page.goto("/depo/transferler/yeni");
    await page.getByRole("combobox", { name: "Hedef depo" }).selectOption({ label: "Raflı Depo (mock) (RAF)" });
    await scanInto(page, "Barkod veya SKU okutun", "MOCK-003-1");
    await page.getByRole("textbox", { name: "MOCK-003-1 miktarı" }).fill("13");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page).toHaveURL(/\/depo\/transferler\/[0-9a-f-]{36}$/);
    // Someone sells 5 meanwhile: the dispatch is refused with the shortage.
    const variantId = api.catalog!.products[2].variants[0].id;
    const row = api.inventory!.balances.find((b) => b.variantId === variantId)!;
    row.reserved = 5;
    row.available = 8;
    await page.getByRole("button", { name: "Gönder", exact: true }).click();
    await page.getByRole("alertdialog", { name: /gönderilsin mi/ }).getByRole("button", { name: "Gönder" }).click();
    const alert = page.getByRole("alert").filter({ hasText: "Transfer gönderilemedi" });
    await expect(alert).toContainText("Kaynakta satılabilir stok yetersiz");
    await expect(alert).toContainText("MOCK-003-1: istenen 13, satılabilir 8");

    await page.getByRole("button", { name: "Taslağı iptal et" }).click();
    const dialog = page.getByRole("alertdialog", { name: /iptal edilsin mi/ });
    await dialog.getByRole("button", { name: "Taslağı iptal et" }).click();
    await expect(dialog.getByText("Sebebi yazın.")).toBeVisible();
    await dialog.getByRole("textbox", { name: "İptal sebebi" }).fill("Yanlış depo seçildi");
    await dialog.getByRole("button", { name: "Taslağı iptal et" }).click();
    await expect(visibleText(page, "İptal")).toBeVisible();
    await expect(page.getByRole("button", { name: "Gönder", exact: true })).toHaveCount(0);
    expect(posts(api, "/cancel")[0].body).toEqual({ reason: "Yanlış depo seçildi" });
  });

  test("izleyici: menüde transfer ve sayım yok, ekranlar yetki mesajı gösterir", async ({ page }) => {
    const api = await signedIn(page, { memberships: [VIEWER], ...setup() });
    await page.goto("/depo/transferler");
    await expect(page.getByRole("heading", { name: "Transferleri görme yetkiniz yok" })).toBeVisible();
    await page.goto("/depo/sayimlar");
    await expect(page.getByRole("heading", { name: "Sayımları görme yetkiniz yok" })).toBeVisible();
    await expect(page.getByRole("navigation").first().getByRole("link", { name: "Transferler" })).toHaveCount(0);
    expect(api.requests.filter((r) => r.path.startsWith("/api/v1/stock-"))).toHaveLength(0);
  });
});

test.describe("raf taşıma (mock)", () => {
  test("stok satırından Taşı: aynı depoda raftan rafa anında taşır", async ({ page }) => {
    const data = setup();
    const variant = data.catalog.products[0].variants[0];
    data.inventory.balances.push({ id: "00000000-0000-4000-8000-9c0000000099", variantId: variant.id, warehouseId: WAREHOUSE_IDS.raf, locationId: LOCATION_IDS.a01, onHand: 8, reserved: 2, available: 6, version: 1, updatedAt: null });
    const api = await signedIn(page, { memberships: [MANAGER], ...data });
    await page.goto(`/stok?warehouseId=${WAREHOUSE_IDS.raf}`);
    await page.getByRole("link", { name: "MOCK-001-1 ürününü başka rafa taşı" }).click();
    await expect(page).toHaveURL(/\/stok\/raf-tasima\?/);
    await expect(page.getByText("Seçili ürün: MOCK-001-1")).toBeVisible();
    await expect(page.getByRole("combobox", { name: "Kaynak raf" })).toHaveValue(LOCATION_IDS.a01);
    await page.getByRole("combobox", { name: "Hedef raf" }).selectOption({ label: "A-02" });
    // Reserved stock does not move: only 6 of 8.
    await page.getByRole("textbox", { name: "Miktar" }).fill("7");
    await page.getByRole("button", { name: "Taşı", exact: true }).click();
    await expect(page.getByText(/^Bu rafta satılabilir yalnızca 6\. Ayrılmış stoğu da taşımak için/)).toBeVisible();
    await page.getByRole("textbox", { name: "Miktar" }).fill("5");
    await page.getByRole("button", { name: "Taşı", exact: true }).click();
    await expect(page.getByText("MOCK-001-1: 5 birim A-01 rafından A-02 rafına taşındı.")).toBeVisible();
    expect(posts(api, "/api/v1/inventory/relocations")[0].body).toEqual({
      warehouseId: WAREHOUSE_IDS.raf,
      reference: null,
      lines: [{ variantId: variant.id, fromLocationId: LOCATION_IDS.a01, toLocationId: LOCATION_IDS.a02, quantity: 5, includeReserved: false }],
    });
    expect(onHand(api, "MOCK-001-1", WAREHOUSE_IDS.raf, LOCATION_IDS.a02)).toBe(5);
    await expectNoSeriousViolations(page);
  });
});

test.describe("sayım (mock)", () => {
  test("kör sayım: yönetici başlatır, personel 360 px'te okutarak sayar ve bitirir, yönetici farkı görür ve hareket uyarısıyla uygular", async ({ page }) => {
    const data = setup();
    const api = await signedIn(page, { memberships: [MANAGER], ...data });
    await page.goto("/depo/sayimlar");
    await expect(page.getByRole("heading", { name: "Bu filtrede sayım yok" })).toBeVisible();
    await page.getByRole("link", { name: "Yeni sayım" }).first().click();
    await expect(page.getByRole("combobox", { name: "Depo" })).toHaveValue(WAREHOUSE_IDS.main);
    await expect(page.getByRole("radio", { name: /Tüm depo/ })).toBeChecked();
    // A warehouse without shelves offers no "Seçili raflar" scope.
    await expect(page.getByRole("radio", { name: /Seçili raflar/ })).toHaveCount(0);
    await page.getByRole("checkbox", { name: "Kör sayım" }).check();
    await page.getByRole("button", { name: "Sayımı başlat" }).click();
    await expect(page).toHaveURL(/\/depo\/sayimlar\/[0-9a-f-]{36}$/);
    expect(posts(api, "/api/v1/stock-counts")[0].body).toEqual({ warehouseId: WAREHOUSE_IDS.main, scope: "full", locationIds: null, variantIds: null, blind: true, note: null });
    const count = api.stockDocuments!.counts[0];
    await expect(page.getByRole("heading", { name: `Sayım ${count.number}` })).toBeVisible();
    // The manager sees system quantities even in a blind count.
    await expect(page.getByRole("table", { name: "Sayım fark raporu" }).getByRole("columnheader", { name: "Sistem" })).toBeVisible();

    // A counter (warehouse staff) on a phone: no system quantities.
    api.memberships[0].role = "warehouse_staff";
    await page.setViewportSize(PHONE);
    await page.reload();
    await expect(page.getByText("Sistemdeki miktarlar gizli.", { exact: false })).toBeVisible();
    const lines = page.getByRole("table", { name: "Sayım satırları" });
    await expect(lines.getByRole("columnheader", { name: "Sistem" })).toHaveCount(0);
    await expect(lines.getByText("11", { exact: true })).toHaveCount(0);
    await expectNoHorizontalScroll(page);

    const scanForm = page.getByRole("form", { name: "Okutarak say" });
    const code = scanForm.getByRole("textbox", { name: "Ürün barkodu veya SKU" });
    const feedback = page.getByTestId("count-feedback");
    expect((await code.boundingBox())!.height).toBeGreaterThanOrEqual(48);
    // Unknown code: nothing counted.
    await code.fill("QQQ-404");
    await code.press("Enter");
    await expect(feedback).toHaveAttribute("data-tone", "error");
    await expect(feedback).toContainText("hiçbir ürünle eşleşmiyor");
    // "Yaz" (set): 9 of MOCK-001-1.
    await scanForm.getByRole("radio", { name: /Yaz/ }).check();
    await scanForm.getByRole("textbox", { name: "Miktar" }).fill("9");
    await code.fill("MOCK-001-1");
    await code.press("Enter");
    await expect(feedback).toHaveText("✓ MOCK-001-1: sayılan 9");
    // "Ekle" (add): one scan per item, 12 of MOCK-002-1 as 10 + 2.
    await scanForm.getByRole("radio", { name: /Ekle/ }).check();
    await scanForm.getByRole("textbox", { name: "Miktar" }).fill("10");
    await code.fill(data.catalog.products[1].variants[0].barcode!);
    await code.press("Enter");
    await expect(feedback).toHaveText("✓ MOCK-002-1: sayılan 10");
    await scanForm.getByRole("textbox", { name: "Miktar" }).fill("2");
    await code.fill("MOCK-002-1");
    await code.press("Enter");
    await expect(feedback).toHaveText("✓ MOCK-002-1: sayılan 12");
    const entries = posts(api, "/entries");
    expect(entries.at(-1)!.body).toEqual({ entries: [{ lineId: null, variantId: null, code: "MOCK-002-1", locationId: null, locationCode: null, quantity: 2, mode: "add" }] });
    expect(entries.at(-1)!.headers["if-match"]).toBeUndefined();
    await expect(page.getByRole("button", { name: "Onayla ve stoğa işle" })).toHaveCount(0);
    await page.getByRole("button", { name: "Sayımı bitir" }).click();
    await page.getByRole("alertdialog", { name: /bitirilsin mi/ }).getByRole("button", { name: "Sayımı bitir" }).click();
    await expect(visibleText(page, "Onay bekliyor")).toBeVisible();
    await expect(page.getByText("Sayım bitirildi; yönetici onayı bekleniyor.")).toBeVisible();
    await expectNoSeriousViolations(page);

    // Back to the manager: the difference report; MOCK-001-1 moved after its snapshot (a sale meanwhile).
    api.memberships[0].role = "manager";
    count.lines.find((l) => l.sku === "MOCK-001-1")!.movedSinceSnapshot = true;
    await page.setViewportSize(DESKTOP);
    await page.reload();
    const report = page.getByRole("table", { name: "Sayım fark raporu" });
    await expect(report.getByRole("row", { name: /MOCK-001-1/ })).toContainText("−2");
    await expect(report.getByRole("row", { name: /MOCK-001-1/ })).toContainText("Sayımdan sonra hareket");
    await expect(page.getByTestId("count-totals")).toContainText("Eksik−2");
    await page.getByRole("button", { name: "Onayla ve stoğa işle" }).click();
    await page.getByRole("alertdialog", { name: /stoğa işlensin mi/ }).getByRole("button", { name: "Onayla" }).click();
    const moved = page.getByRole("alertdialog", { name: "Bazı satırların stoğu sayımdan sonra değişti" });
    await expect(moved.getByRole("list", { name: "Hareket gören satırlar" })).toHaveText("MOCK-001-1");
    await moved.getByRole("button", { name: "Yine de uygula" }).click();
    await expect(visibleText(page, "Uygulandı").first()).toBeVisible();
    const applies = posts(api, "/apply");
    expect(applies.map((r) => r.body)).toEqual([{ acceptMovedLines: false }, { acceptMovedLines: true }]);
    expect(applies[0].headers["idempotency-key"]).not.toBe(applies[1].headers["idempotency-key"]);
    expect(onHand(api, "MOCK-001-1", WAREHOUSE_IDS.main)).toBe(9);
    expect(onHand(api, "MOCK-002-1", WAREHOUSE_IDS.main)).toBe(12);
    // The uncounted line changed nothing.
    expect(onHand(api, "MOCK-003-1", WAREHOUSE_IDS.main)).toBe(13);
    await expect(report.getByRole("row", { name: /MOCK-003-1/ })).toContainText("Sayılmadan kapandı");
    await expect(report.getByRole("row", { name: /MOCK-001-1/ })).toContainText("Düzeltme −2");
  });

  test("onay rezervin altına inemez: count.below_reserved satırları Türkçe gösterilir", async ({ page }) => {
    const data = setup();
    const api = await signedIn(page, { memberships: [MANAGER], ...data });
    await page.goto("/depo/sayimlar/yeni");
    await page.getByRole("button", { name: "Sayımı başlat" }).click();
    await expect(page).toHaveURL(/\/depo\/sayimlar\/[0-9a-f-]{36}$/);
    // Line by line: MOCK-002-1 (2 reserved) counted as 1.
    await page.getByRole("textbox", { name: "MOCK-002-1 sayılan miktar" }).fill("1");
    await page.getByRole("button", { name: "Girilenleri kaydet (1)" }).click();
    await expect(page.getByRole("button", { name: "Girilenleri kaydet (0)" })).toBeDisabled();
    expect(posts(api, "/entries")[0].body).toEqual({ entries: [{ lineId: api.stockDocuments!.counts[0].lines[1].id, variantId: null, code: null, locationId: null, locationCode: null, quantity: 1, mode: "set" }] });
    await page.getByRole("button", { name: "Sayımı bitir" }).click();
    await page.getByRole("alertdialog", { name: /bitirilsin mi/ }).getByRole("button", { name: "Sayımı bitir" }).click();
    await page.getByRole("button", { name: "Onayla ve stoğa işle" }).click();
    await page.getByRole("alertdialog", { name: /stoğa işlensin mi/ }).getByRole("button", { name: "Onayla" }).click();
    const alert = page.getByRole("alert").filter({ hasText: "Sayım uygulanamadı" });
    await expect(alert).toContainText("rezerve");
    await expect(alert).toContainText("MOCK-002-1: ayrılmış 2, sayım sonrası rafta 1");
    expect(onHand(api, "MOCK-002-1", WAREHOUSE_IDS.main)).toBe(12);
  });
});
