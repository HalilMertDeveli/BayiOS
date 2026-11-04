/**
 * Stock (/stok), variant stock card, adjustment, warehouses (/depo) and goods receipt (/depo/mal-kabul) — #28 web.
 * MOCK: Supabase Auth and the BayiOS API (incl. in-memory Catalog + Inventory/Warehouse) are faked with page.route —
 * support/mock-backend.ts, support/mock-catalog.ts, support/mock-inventory.ts. All data is synthetic; nothing here
 * talks to a real API or database.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog, problemBody, fulfillJson, type MockCatalog } from "./support/mock-catalog";
import { LOCATION_IDS, WAREHOUSE_IDS, busyResponse, createMockInventory, type MockInventory } from "./support/mock-inventory";

test.use({ viewport: DESKTOP });

const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const variantOf = (catalog: MockCatalog, n: number) => catalog.products[n - 1].variants[0];

function setup(options: { products?: number; stocked?: number; policy?: MockInventory["policy"] } = {}) {
  const catalog = createMockCatalog({ products: options.products ?? 3 });
  const inventory = createMockInventory(catalog, { stocked: options.stocked, policy: options.policy });
  return { catalog, inventory };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const requestsTo = (api: Api, method: string, path: string) => api.requests.filter((r) => r.method === method && r.path === path);

test.describe("stok listesi (mock)", () => {
  test("sayfalama: ilk 50 bakiye, 'Daha fazla yükle' imleçle devamını getirir; varyant SKU'ları gösterilir", async ({ page }) => {
    const { catalog, inventory } = setup({ products: 60, stocked: 60 });
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/stok");
    const table = page.getByRole("table", { name: "Stok bakiyeleri" });
    await expect(table.getByRole("row")).toHaveCount(51);
    await expect(page.getByText("50 kayıt gösteriliyor (devamı var)")).toBeVisible();
    await expect(table.getByRole("link", { name: "MOCK-001-1", exact: true })).toBeVisible();
    const first = new URLSearchParams(requestsTo(api, "GET", "/api/v1/inventory/balances")[0].search);
    expect(first.get("limit")).toBe("50");
    expect(first.has("cursor")).toBe(false);

    await page.getByRole("button", { name: "Daha fazla yükle" }).click();
    await expect(table.getByRole("row")).toHaveCount(61);
    await expect(page.getByText("60 kayıt gösteriliyor", { exact: true })).toBeVisible();
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/inventory/balances").at(-1)!.search).get("cursor")).toBe("o:50");
    // Rafta / ayrılmış / satılabilir of balance 1: 11 / 1 / 10.
    await expect(table.getByRole("row").nth(1)).toContainText("11");
    await expect(table.getByRole("row").nth(1)).toContainText("10");
  });

  test("filtreler: depo seçimi ve SKU/barkod (tam eşleşme) URL'de tutulur", async ({ page }) => {
    const { catalog, inventory } = setup({ products: 5, stocked: 5 });
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/stok");
    const table = page.getByRole("table", { name: "Stok bakiyeleri" });
    await expect(table.getByRole("row")).toHaveCount(6);

    await page.getByRole("combobox", { name: "Depo", exact: true }).selectOption({ label: "Raflı Depo (mock) (RAF)" });
    await expect(page).toHaveURL(new RegExp(`warehouseId=${WAREHOUSE_IDS.raf}`));
    await expect(page.getByRole("heading", { name: "Bu filtrede stok kaydı yok" })).toBeVisible();
    await page.getByRole("button", { name: "Filtreleri temizle" }).first().click();
    await expect(table.getByRole("row")).toHaveCount(6);

    // Scanner-style: type + Enter, exact SKU.
    await page.getByRole("textbox", { name: "SKU veya barkod" }).fill("MOCK-003-1");
    await page.getByRole("textbox", { name: "SKU veya barkod" }).press("Enter");
    await expect(page).toHaveURL(/q=MOCK-003-1/);
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table).toContainText("MOCK-003-1");
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/inventory/balances").at(-1)!.search).get("variantId")).toBe(variantOf(catalog, 3).id);

    // Barcode resolves through /variants/lookup.
    await page.getByRole("textbox", { name: "SKU veya barkod" }).fill(variantOf(catalog, 2).barcode!);
    await page.getByRole("textbox", { name: "SKU veya barkod" }).press("Enter");
    await expect(table).toContainText("MOCK-002-1");

    await page.getByRole("textbox", { name: "SKU veya barkod" }).fill("YOK-999");
    await page.getByRole("textbox", { name: "SKU veya barkod" }).press("Enter");
    await expect(page.getByRole("heading", { name: "Ürün bulunamadı" })).toBeVisible();
  });

  test("sık görünüm ve mobil kartlar; yatay kaydırma yok", async ({ page }) => {
    const { catalog, inventory } = setup();
    await signedIn(page, { catalog, inventory });
    await page.goto("/stok");
    const dense = page.getByRole("button", { name: "Sık görünüm" });
    await dense.click();
    await expect(dense).toHaveAttribute("aria-pressed", "true");

    await page.setViewportSize(MOBILE);
    const cards = page.getByRole("list", { name: "Stok bakiyeleri" });
    await expect(cards.getByRole("listitem")).toHaveCount(3);
    await expect(cards.getByRole("listitem").first()).toContainText("Satılabilir");
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
});

test.describe("varyant stok kartı (mock)", () => {
  test("/stok/varyant/[id]: bakiyeler, hareket geçmişi ve tür filtresi", async ({ page }) => {
    const { catalog, inventory } = setup();
    const v = variantOf(catalog, 1);
    inventory.movements.push(
      { id: "00000000-0000-4000-8000-9d0000000901", variantId: v.id, warehouseId: WAREHOUSE_IDS.main, locationId: null, type: "receipt", quantity: 12, unitCost: null, sourceType: "receipt", sourceId: null, sourceLineId: null, reason: "IRS-1 (mock)", balanceAfter: 12, correlationId: null, occurredAt: "2026-11-02T08:00:00Z", createdBy: null },
      { id: "00000000-0000-4000-8000-9d0000000902", variantId: v.id, warehouseId: WAREHOUSE_IDS.main, locationId: null, type: "damage_out", quantity: -1, unitCost: null, sourceType: "adjustment", sourceId: null, sourceLineId: null, reason: "Kırık (mock)", balanceAfter: 11, correlationId: null, occurredAt: "2026-11-03T08:00:00Z", createdBy: null },
    );
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/stok");
    await page.getByRole("table", { name: "Stok bakiyeleri" }).getByRole("link", { name: "MOCK-001-1", exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/stok/varyant/${v.id}$`));
    await expect(page.getByRole("heading", { level: 1, name: "MOCK-001-1" })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Ana menü" }).getByRole("link", { name: "Stok" })).toHaveAttribute("aria-current", "page");
    const history = page.getByRole("table", { name: "Hareket geçmişi" });
    await expect(history.getByRole("row")).toHaveCount(3);
    await expect(history.getByRole("row").nth(1)).toContainText("Hasar / fire");
    await expect(history.getByRole("row").nth(1)).toContainText("−1");

    await page.getByLabel("Hareket türü").selectOption({ label: "Mal kabul" });
    await expect(history.getByRole("row")).toHaveCount(2);
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/inventory/movements").at(-1)!.search).get("type")).toBe("receipt");
    await expectNoSeriousViolations(page);

    // Direct load of the dynamic segment (#87).
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: "MOCK-001-1" })).toBeVisible();
  });
});

test.describe("mal kabul (mock)", () => {
  test("barkod okutma (+Enter) satır ekler, tekrar okutma miktarı artırır; yanıt kaybolunca aynı Idempotency-Key ile tekrar", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/depo");
    await page.getByRole("link", { name: "Mal kabul" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Mal kabul" })).toBeVisible();
    await expect(page.getByRole("combobox", { name: "Depo", exact: true })).toHaveValue(WAREHOUSE_IDS.main);

    const scanner = page.getByLabel("Barkod veya SKU okutun");
    // Keyboard-wedge scanner: fast typing + Enter.
    await scanner.pressSequentially(variantOf(catalog, 1).barcode!, { delay: 5 });
    await scanner.press("Enter");
    await expect(page.getByRole("status").filter({ hasText: "MOCK-001-1 eklendi." })).toBeVisible();
    await expect(scanner).toBeFocused();
    await scanner.fill(variantOf(catalog, 1).barcode!);
    await scanner.press("Enter");
    await scanner.fill("MOCK-002-1");
    await scanner.press("Enter");
    const lines = page.getByRole("list", { name: "Mal kabul satırları" }).getByRole("listitem");
    await expect(lines).toHaveCount(2);
    await expect(page.getByLabel("MOCK-001-1 miktarı")).toHaveValue("2");
    await page.getByLabel("MOCK-002-1 miktarı").fill("3,5");
    await page.getByLabel("Referans (irsaliye / fatura no)").fill("IRS-2026-001 (mock)");

    // The server applies the receipt but the response never arrives.
    inventory.dropNextResponse = true;
    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Mal kabul kaydedilemedi" })).toBeVisible();
    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Mal kabul kaydedildi" })).toBeVisible();
    await expect(page.getByText("2 satır, toplam 5,5 birim stoğa girdi.")).toBeVisible();

    const posts = requestsTo(api, "POST", "/api/v1/inventory/receipts");
    expect(posts).toHaveLength(2);
    expect(posts[0].headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(posts[1].headers["idempotency-key"]).toBe(posts[0].headers["idempotency-key"]);
    expect(posts[0].body).toEqual({
      warehouseId: WAREHOUSE_IDS.main,
      reference: "IRS-2026-001 (mock)",
      lines: [
        { variantId: variantOf(catalog, 1).id, locationId: null, quantity: 2, unitCost: null },
        { variantId: variantOf(catalog, 2).id, locationId: null, quantity: 3.5, unitCost: null },
      ],
    });
    // Applied exactly once (the retry was a replay).
    expect(inventory.movements).toHaveLength(2);
    expect(inventory.balances.find((b) => b.variantId === variantOf(catalog, 1).id)!.onHand).toBe(13);
    await expect(lines).toHaveCount(0);

    // The next receipt is a new attempt with a new key.
    await scanner.fill("MOCK-003-1");
    await scanner.press("Enter");
    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByText("1 satır, toplam 1 birim stoğa girdi.")).toBeVisible();
    expect(requestsTo(api, "POST", "/api/v1/inventory/receipts")[2].headers["idempotency-key"]).not.toBe(posts[0].headers["idempotency-key"]);
  });

  test("lokasyonlu depo: raf zorunlu; aramada birden çok sonuç seçtirilir; API alan hatası satıra yazılır", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/depo/mal-kabul");
    await page.getByRole("combobox", { name: "Depo", exact: true }).selectOption(WAREHOUSE_IDS.raf);
    const scanner = page.getByLabel("Barkod veya SKU okutun");
    await scanner.fill("mock-00");
    await scanner.press("Enter");
    const results = page.getByRole("list", { name: "Bulunan ürünler" });
    await expect(results.getByRole("button")).toHaveCount(3);
    await results.getByRole("button", { name: /MOCK-002-1/ }).click();

    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByLabel("MOCK-002-1 rafı")).toHaveAttribute("aria-invalid", "true");
    expect(requestsTo(api, "POST", "/api/v1/inventory/receipts")).toHaveLength(0);

    await page.getByLabel("MOCK-002-1 rafı").selectOption(LOCATION_IDS.a01);
    inventory.overrides.set("POST /api/v1/inventory/receipts", (route) =>
      fulfillJson(route, 422, problemBody(422, "validation.failed", { errors: { "lines[0].quantity": ["The variant's unit does not allow fractions."] } })),
    );
    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByLabel("MOCK-002-1 miktarı")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByText("Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin.")).toBeVisible();

    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Mal kabul kaydedildi" })).toBeVisible();
    expect(requestsTo(api, "POST", "/api/v1/inventory/receipts").at(-1)!.body).toMatchObject({
      warehouseId: WAREHOUSE_IDS.raf,
      lines: [{ variantId: variantOf(catalog, 2).id, locationId: LOCATION_IDS.a01, quantity: 1 }],
    });
  });

  test("inventory.busy: Retry-After sonrası aynı anahtarla tek otomatik deneme", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/depo/mal-kabul");
    const scanner = page.getByLabel("Barkod veya SKU okutun");
    await scanner.fill("MOCK-001-1");
    await scanner.press("Enter");
    inventory.overrides.set("POST /api/v1/inventory/receipts", busyResponse);
    await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Mal kabul kaydedildi" })).toBeVisible();
    const posts = requestsTo(api, "POST", "/api/v1/inventory/receipts");
    expect(posts).toHaveLength(2);
    expect(posts[1].headers["idempotency-key"]).toBe(posts[0].headers["idempotency-key"]);
    expect(inventory.movements).toHaveLength(1);
  });

  test("mobil: büyük dokunma hedefleri, alt sabit kaydet düğmesi, yatay kaydırma yok, axe", async ({ page }) => {
    await page.setViewportSize(MOBILE);
    const { catalog, inventory } = setup();
    await signedIn(page, { catalog, inventory });
    await page.goto("/depo/mal-kabul");
    const scanner = page.getByLabel("Barkod veya SKU okutun");
    await scanner.fill("MOCK-001-1");
    await scanner.press("Enter");
    await expect(page.getByLabel("MOCK-001-1 miktarı")).toBeVisible();
    for (const control of [scanner, page.getByLabel("MOCK-001-1 miktarı"), page.getByRole("button", { name: "MOCK-001-1 satırını kaldır" }), page.getByRole("button", { name: "Mal kabulü kaydet" })]) {
      const box = await control.boundingBox();
      expect(box!.height).toBeGreaterThanOrEqual(44);
    }
    await expect(page.getByRole("button", { name: "Mal kabulü kaydet" })).toBeInViewport();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    await expectNoSeriousViolations(page);
  });
});

test.describe("stok düzeltme (mock)", () => {
  test("warn politikası: açık onay diyaloğu; onaydan sonra confirmNegativeStock=true ile yeniden gönderilir", async ({ page }) => {
    const { catalog, inventory } = setup({ policy: "warn" });
    const v = variantOf(catalog, 1);
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/stok");
    await page.getByRole("link", { name: "MOCK-001-1 stoğunu düzelt" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Stok düzeltme" })).toBeVisible();
    await expect(page.getByRole("form", { name: "Stok düzeltme" }).getByText("MOCK-001-1", { exact: true })).toBeVisible();
    await expect(page.getByText(/Mevcut: rafta 11, ayrılmış 1, satılabilir/)).toBeVisible();

    await page.getByLabel("Düzeltme türü").selectOption("damage_out");
    await page.getByLabel("Miktar").fill("15");
    await page.getByLabel("Sebep").fill("Sel hasarı (mock)");
    await page.getByRole("button", { name: "Düzeltmeyi kaydet" }).click();
    const dialog = page.getByRole("alertdialog", { name: "Stok eksiye düşecek" });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText("istenen 15, satılabilir 10");
    await expectNoSeriousViolations(page);

    await dialog.getByRole("button", { name: "Vazgeç" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByText("Düzeltme kaydedilmedi: eksi stok onaylanmadı.")).toBeVisible();
    expect(inventory.movements).toHaveLength(0);

    await page.getByRole("button", { name: "Düzeltmeyi kaydet" }).click();
    await dialog.getByRole("button", { name: "Yine de kaydet" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Stok düzeltildi" })).toBeVisible();
    await expect(page.getByText(/MOCK-001-1 için yeni rafta bakiye: [-−]4./)).toBeVisible();

    const posts = requestsTo(api, "POST", "/api/v1/inventory/adjustments");
    expect(posts.map((p) => (p.body as { confirmNegativeStock: boolean }).confirmNegativeStock)).toEqual([false, false, true]);
    expect(posts[0].body).toEqual({
      variantId: v.id,
      warehouseId: WAREHOUSE_IDS.main,
      locationId: null,
      type: "damage_out",
      quantity: 15,
      reason: "Sel hasarı (mock)",
      confirmNegativeStock: false,
    });
    // Same body → same key; the confirmed body is a new attempt → new key.
    expect(posts[1].headers["idempotency-key"]).toBe(posts[0].headers["idempotency-key"]);
    expect(posts[2].headers["idempotency-key"]).not.toBe(posts[0].headers["idempotency-key"]);
    expect(inventory.movements).toHaveLength(1);
  });

  test("block politikası: rezerve stok düşülemez mesajı satılabilir miktarla gösterilir", async ({ page }) => {
    const { catalog, inventory } = setup({ policy: "block" });
    await signedIn(page, { catalog, inventory });
    await page.goto(`/stok/duzeltme?variantId=${variantOf(catalog, 1).id}&warehouseId=${WAREHOUSE_IDS.main}`);
    await expect(page.getByRole("form", { name: "Stok düzeltme" }).getByText("MOCK-001-1", { exact: true })).toBeVisible();
    await page.getByLabel("Düzeltme türü").selectOption("adjustment_out");
    await page.getByLabel("Miktar").fill("11");
    await page.getByLabel("Sebep").fill("Sayım farkı (mock)");
    await page.getByRole("button", { name: "Düzeltmeyi kaydet" }).click();
    const alert = page.getByRole("alert").filter({ hasText: "Düzeltme kaydedilemedi" });
    await expect(alert).toContainText("Satılabilir stok yetersiz");
    await expect(alert).toContainText("Satılabilir: 10, istenen: 11.");
    await expect(page.getByRole("alertdialog")).toHaveCount(0);
  });

  test("inventory.busy iki kez: tek otomatik denemeden sonra açık mesaj", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { catalog, inventory });
    await page.goto(`/stok/duzeltme?variantId=${variantOf(catalog, 1).id}`);
    await expect(page.getByRole("form", { name: "Stok düzeltme" }).getByText("MOCK-001-1", { exact: true })).toBeVisible();
    await page.getByLabel("Düzeltme türü").selectOption("adjustment_in");
    await page.getByLabel("Miktar").fill("2");
    await page.getByLabel("Sebep").fill("Sayım fazlası (mock)");
    const key = "POST /api/v1/inventory/adjustments";
    inventory.overrides.set(key, (route) => {
      inventory.overrides.set(key, busyResponse);
      return busyResponse(route);
    });
    await page.getByRole("button", { name: "Düzeltmeyi kaydet" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "hâlâ başka bir işlem tarafından kullanılıyor" })).toBeVisible();
    const posts = requestsTo(api, "POST", "/api/v1/inventory/adjustments");
    expect(posts).toHaveLength(2);
    expect(posts[1].headers["idempotency-key"]).toBe(posts[0].headers["idempotency-key"]);
  });
});

test.describe("depo yönetimi (mock)", () => {
  test("depo ekle/düzenle: kod çakışması, stok varken raf takibi değişmez, varsayılan depo pasif yapılamaz", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/depo");
    const table = page.getByRole("table", { name: "Depolar" });
    await expect(table.getByRole("row")).toHaveCount(3);
    await expectNoSeriousViolations(page);

    await page.getByRole("button", { name: "Yeni depo" }).click();
    let dialog = page.getByRole("dialog", { name: "Yeni depo" });
    await dialog.getByLabel("Depo kodu").fill("MAIN");
    await dialog.getByLabel("Depo adı").fill("Kopya (mock)");
    await dialog.getByRole("button", { name: "Depoyu ekle" }).click();
    await expect(dialog.getByRole("alert")).toContainText("Bu depo kodu zaten kullanılıyor");
    await dialog.getByLabel("Depo kodu").fill("DUKKAN");
    await dialog.getByLabel("Depo adı").fill("Dükkân Arka Depo (mock)");
    await dialog.getByRole("button", { name: "Depoyu ekle" }).click();
    await expect(dialog).toBeHidden();
    await expect(table.getByRole("row")).toHaveCount(4);
    const creates = requestsTo(api, "POST", "/api/v1/warehouses");
    expect(creates.at(-1)!.headers["idempotency-key"]).toBeTruthy();
    expect(creates.at(-1)!.body).toEqual({ code: "DUKKAN", name: "Dükkân Arka Depo (mock)", usesLocations: false });

    await page.getByRole("button", { name: "Ana Depo (mock) deposunu düzenle" }).click();
    dialog = page.getByRole("dialog", { name: /Depoyu düzenle/ });
    await expect(dialog.getByLabel("Aktif")).toBeDisabled();
    await expect(dialog).toContainText("Varsayılan depo pasif yapılamaz");
    await dialog.getByLabel("Raf/göz (lokasyon) takibi").check();
    await dialog.getByRole("button", { name: "Kaydet" }).click();
    await expect(dialog.getByRole("alert")).toContainText("Depoda stok varken raf/göz takibi açılıp kapatılamaz");
    const patch = requestsTo(api, "PATCH", `/api/v1/warehouses/${WAREHOUSE_IDS.main}`)[0];
    expect(patch.headers["if-match"]).toBe('W/"1"');
    expect(patch.headers["content-type"]).toContain("application/merge-patch+json");
    expect(patch.body).toEqual({ usesLocations: true });
    await dialog.getByRole("button", { name: "Vazgeç" }).click();

    // A stale/other client deactivating the default warehouse gets the API's rule, surfaced as is.
    inventory.overrides.set(`PATCH /api/v1/warehouses/${WAREHOUSE_IDS.raf}`, (route) => fulfillJson(route, 409, problemBody(409, "warehouse.default_inactive")));
    await page.getByRole("button", { name: "Raflı Depo (mock) deposunu düzenle" }).click();
    dialog = page.getByRole("dialog", { name: /Depoyu düzenle/ });
    await dialog.getByLabel("Aktif").uncheck();
    await dialog.getByRole("button", { name: "Kaydet" }).click();
    await expect(dialog.getByRole("alert")).toContainText("Varsayılan depo pasif yapılamaz. Önce başka bir depoyu varsayılan yapın.");
  });

  test("lokasyonlar: listele, ekle, pasifleştir (If-Match)", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { catalog, inventory });
    await page.goto("/depo");
    await expect(page.getByRole("heading", { name: "Bu depo raf/göz takibi kullanmıyor" })).toBeVisible();
    await page.getByRole("button", { name: "Lokasyonlar — Raflı Depo (mock)" }).click();
    await expect(page).toHaveURL(new RegExp(`depo=${WAREHOUSE_IDS.raf}`));
    const locations = page.getByRole("table", { name: "Raflı Depo (mock) lokasyonları" });
    await expect(locations.getByRole("row")).toHaveCount(3);

    await page.getByRole("button", { name: "Yeni lokasyon" }).click();
    const dialog = page.getByRole("dialog", { name: /Yeni lokasyon/ });
    await dialog.getByLabel("Lokasyon kodu").fill("A-03");
    await dialog.getByLabel("Toplama sırası").fill("30");
    await dialog.getByRole("button", { name: "Lokasyonu ekle" }).click();
    await expect(dialog).toBeHidden();
    await expect(locations.getByRole("row")).toHaveCount(4);
    expect(requestsTo(api, "POST", `/api/v1/warehouses/${WAREHOUSE_IDS.raf}/locations`)[0].body).toEqual({ code: "A-03", barcode: null, pickSequence: 30 });

    await page.getByRole("button", { name: "A-02 lokasyonunu düzenle" }).click();
    const edit = page.getByRole("dialog", { name: /Lokasyonu düzenle/ });
    await edit.getByLabel("Aktif").uncheck();
    await edit.getByRole("button", { name: "Kaydet" }).click();
    await expect(edit).toBeHidden();
    await expect(locations.getByRole("row").filter({ hasText: "A-02" })).toContainText("Pasif");
    const patch = requestsTo(api, "PATCH", `/api/v1/locations/${LOCATION_IDS.a02}`)[0];
    expect(patch.body).toEqual({ isActive: false });
    expect(patch.headers["if-match"]).toBe('W/"1"');
  });
});

test.describe("yetkiler (mock)", () => {
  test("satış: stok ve depo salt okunur; yazma ekranları kapalı; yazma isteği yok", async ({ page }) => {
    const { catalog, inventory } = setup();
    const api = await signedIn(page, { memberships: [SALES], catalog, inventory });
    await page.goto("/stok");
    await expect(page.getByRole("table", { name: "Stok bakiyeleri" }).getByRole("row")).toHaveCount(4);
    await expect(page.getByRole("link", { name: "Mal kabul" })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Stok düzelt" })).toHaveCount(0);
    await expect(page.getByRole("link", { name: /stoğunu düzelt/ })).toHaveCount(0);

    await page.goto("/depo");
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByRole("button", { name: "Yeni depo" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /deposunu düzenle/ })).toHaveCount(0);

    await page.goto("/depo/mal-kabul");
    await expect(page.getByRole("heading", { name: "Mal kabul yetkiniz yok" })).toBeVisible();
    await page.goto("/stok/duzeltme");
    await expect(page.getByRole("heading", { name: "Stok düzeltme yetkiniz yok" })).toBeVisible();
    await page.goto(`/stok/varyant/${variantOf(catalog, 1).id}`);
    await expect(page.getByRole("heading", { name: "Hareket geçmişini görme yetkiniz yok" })).toBeVisible();
    expect(api.requests.some((r) => r.method !== "GET")).toBe(false);
    expect(requestsTo(api, "GET", "/api/v1/inventory/movements")).toHaveLength(0);
  });

  test("depo personeli: mal kabul yapabilir, depo tanımlarını ve düzeltmeyi göremez", async ({ page }) => {
    const { catalog, inventory } = setup();
    await signedIn(page, { memberships: [STAFF], catalog, inventory });
    await page.goto("/depo");
    await expect(page.getByRole("link", { name: "Mal kabul" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Yeni depo" })).toHaveCount(0);
    await page.goto("/stok");
    await expect(page.getByRole("link", { name: "Mal kabul" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Stok düzelt" })).toHaveCount(0);
    await page.goto("/stok/duzeltme");
    await expect(page.getByRole("heading", { name: "Stok düzeltme yetkiniz yok" })).toBeVisible();
  });
});

test.describe("erişilebilirlik — stok ve depo sayfaları (mock)", () => {
  for (const [name, viewport] of [
    ["desktop", DESKTOP],
    ["mobile", MOBILE],
  ] as const) {
    for (const route of ["/stok", "/stok/duzeltme", "/depo", "/depo/mal-kabul"]) {
      test(`${route} (${name})`, async ({ page }) => {
        await page.setViewportSize(viewport);
        const { catalog, inventory } = setup();
        await signedIn(page, { catalog, inventory });
        await page.goto(route);
        await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
        await expect(page.getByText("Yükleniyor…")).toHaveCount(0);
        await expectNoSeriousViolations(page);
      });
    }
  }
});
