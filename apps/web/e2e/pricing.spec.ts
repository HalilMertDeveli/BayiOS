/**
 * Price lists (/fiyat-listeleri), list detail + items editor (/fiyat-listeleri/[id]) and the price quote
 * (/fiyat-listeleri/sorgula) — Pricing API (#78) web screens.
 * MOCK: Supabase Auth and the BayiOS API (incl. in-memory Catalog and Customers/Pricing) are faked with page.route —
 * support/mock-backend.ts, support/mock-commerce.ts. All data and prices are synthetic; nothing here talks to a real
 * API or database.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog, fulfillJson, problemBody } from "./support/mock-catalog";
import { PRICE_LIST_IDS, createMockCommerce } from "./support/mock-commerce";

test.use({ viewport: DESKTOP });

const as = (role: string): MockMembership => ({ ...TENANT_A, role });

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

function setup() {
  const catalog = createMockCatalog({ products: 3 });
  const commerce = createMockCommerce(catalog, { customers: 3 });
  return { catalog, commerce };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const requestsTo = (api: Api, method: string, path: string) => api.requests.filter((r) => r.method === method && r.path === path);

async function scan(page: Page, code: string) {
  const input = page.getByRole("textbox", { name: "Ürün okut veya ara" });
  await input.fill(code);
  await input.press("Enter");
  await expect(page.getByText(`${code} eklendi; fiyatını girin.`)).toBeVisible();
}

test.describe("fiyat listeleri (mock)", () => {
  test("liste: varsayılan önce; alış listesi yalnızca owner/admin'e görünür", async ({ page }) => {
    const { catalog, commerce } = setup();
    await signedIn(page, { catalog, commerce });
    await page.goto("/fiyat-listeleri");
    const table = page.getByRole("table", { name: "Fiyat listeleri" });
    await expect(table.getByRole("row")).toHaveCount(4);
    await expect(table.getByRole("row").nth(1)).toContainText("Perakende (mock)");
    await expect(table.getByRole("row").nth(1)).toContainText("Varsayılan");
    await expect(table).toContainText("Alış Listesi (mock)");
    await expectNoSeriousViolations(page);
  });

  test("manager: alış listesi gizli, salt okunur (yeni liste yok)", async ({ page }) => {
    const { catalog, commerce } = setup();
    await signedIn(page, { catalog, commerce, memberships: [as("manager")] });
    await page.goto("/fiyat-listeleri");
    const table = page.getByRole("table", { name: "Fiyat listeleri" });
    await expect(table.getByRole("row")).toHaveCount(3);
    await expect(table).not.toContainText("Alış");
    await expect(page.getByRole("button", { name: "Yeni liste" })).toHaveCount(0);
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
  });

  test("yeni liste → detay; kademeli kalemler toplu kaydedilir (decimal string, Idempotency-Key)", async ({ page }) => {
    const { catalog, commerce } = setup();
    const api = await signedIn(page, { catalog, commerce });
    await page.goto("/fiyat-listeleri");
    await page.getByRole("button", { name: "Yeni liste" }).click();
    const dialog = page.getByRole("dialog", { name: "Yeni fiyat listesi" });
    await dialog.getByLabel("Liste adı").fill("Kampanya (mock)");
    await dialog.getByRole("combobox", { name: "Taban liste" }).selectOption({ label: "Perakende (mock)" });
    await dialog.getByLabel("İskonto (%)").fill("12,5");
    await dialog.getByRole("button", { name: "Listeyi oluştur" }).click();
    await expect(page).toHaveURL(/\/fiyat-listeleri\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: "Kampanya (mock)" })).toBeVisible();
    await expect(page.getByText("Türetilmiş liste")).toBeVisible();
    const create = requestsTo(api, "POST", "/api/v1/price-lists")[0];
    expect(create.headers["idempotency-key"]).toBeTruthy();
    expect(create.body).toMatchObject({ name: "Kampanya (mock)", kind: "sales", currency: "TRY", basePriceListId: PRICE_LIST_IDS.perakende, discountPercent: "12.5" });

    // #96: the list page was kept mounted; going back shows it without the create dialog, and a new one starts empty.
    await page.getByRole("link", { name: "Fiyat listeleri", exact: true }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: "Fiyat listeleri" })).toBeVisible();
    await expect(page.getByRole("dialog", { name: "Yeni fiyat listesi" })).toHaveCount(0);
    await page.getByRole("button", { name: "Yeni liste" }).click();
    await expect(page.getByRole("dialog", { name: "Yeni fiyat listesi" }).getByLabel("Liste adı")).toHaveValue("");
    await page.getByRole("dialog", { name: "Yeni fiyat listesi" }).getByRole("button", { name: "Vazgeç" }).click();
    await page.goBack();
    await expect(page.getByRole("heading", { level: 1, name: "Kampanya (mock)" })).toBeVisible();

    await expect(page.getByRole("heading", { name: "Bu listede henüz fiyat yok" })).toBeVisible();
    await scan(page, "MOCK-001-1");
    await page.getByLabel("1. satır birim fiyat").fill("12,50");
    // Same product again = a new quantity tier; its minimum quantity is left empty for the user.
    await scan(page, "MOCK-001-1");
    await expect(page.getByLabel("2. satır en az miktar")).toHaveValue("");
    await page.getByLabel("2. satır en az miktar").fill("10");
    await page.getByLabel("2. satır birim fiyat").fill("11");
    await page.getByRole("button", { name: "2 satırı kaydet" }).click();
    await expect(page.getByText("2 yeni, 0 güncellenen kalem.")).toBeVisible();
    const id = new URL(page.url()).pathname.split("/").at(-1)!;
    const put = requestsTo(api, "PUT", `/api/v1/price-lists/${id}/items`)[0];
    expect(put.headers["idempotency-key"]).toBeTruthy();
    expect(put.body).toEqual({
      items: [
        { variantId: catalog.products[0].variants[0].id, minQuantity: "1", unitPrice: "12.50" },
        { variantId: catalog.products[0].variants[0].id, minQuantity: "10", unitPrice: "11" },
      ],
    });
    const items = page.getByRole("table", { name: "Kampanya (mock) fiyatları" });
    await expect(items.getByRole("row")).toHaveCount(3);
    await expect(items.getByRole("row").nth(1)).toContainText("MOCK-001-1");
    await expect(items.getByRole("row").nth(1)).toContainText("₺12,50");
    await expect(items.getByRole("row").nth(2)).toContainText("₺11,00");
    await expectNoSeriousViolations(page);
  });

  test("422 satır hataları ilgili satıra yazılır; istemci tekrar eden kademeyi yakalar", async ({ page }) => {
    const { catalog, commerce } = setup();
    const api = await signedIn(page, { catalog, commerce });
    await page.goto(`/fiyat-listeleri/${PRICE_LIST_IDS.bayiA}`);
    await expect(page.getByRole("table", { name: "Bayi A Listesi (mock) fiyatları" }).getByRole("row")).toHaveCount(5);

    // Client check: same product + same minimum quantity twice.
    await scan(page, "MOCK-003-1");
    await page.getByLabel("1. satır birim fiyat").fill("5");
    await scan(page, "MOCK-003-1");
    await page.getByLabel("2. satır en az miktar").fill("1,000");
    await page.getByLabel("2. satır birim fiyat").fill("4");
    await page.getByRole("button", { name: "2 satırı kaydet" }).click();
    await expect(page.getByText("Bu ürün için bu en az miktar zaten 1. satırda var.")).toBeVisible();
    expect(requestsTo(api, "PUT", `/api/v1/price-lists/${PRICE_LIST_IDS.bayiA}/items`)).toHaveLength(0);

    // Server 422 on row 2: marked on that row, nothing saved.
    await page.getByLabel("2. satır en az miktar").fill("5");
    commerce.overrides.set(`PUT /api/v1/price-lists/${PRICE_LIST_IDS.bayiA}/items`, (route) =>
      fulfillJson(route, 422, problemBody(422, "validation.failed", { errors: { "items[1].unitPrice": ["Required: 0 or more with at most 4 decimals."] } })),
    );
    await page.getByRole("button", { name: "2 satırı kaydet" }).click();
    const row2 = page.getByRole("listitem", { name: "2. satır: MOCK-003-1" });
    await expect(row2.getByLabel("2. satır birim fiyat")).toHaveAttribute("aria-invalid", "true");
    await expect(row2).toContainText("0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin.");
    await expect(page.getByRole("alert").filter({ hasText: "Fiyatlar kaydedilemedi" })).toBeVisible();
    await expect(page.getByLabel("1. satır birim fiyat")).not.toHaveAttribute("aria-invalid", "true");

    // Retry goes through (the mock override was one-shot); existing tier repriced via "Değiştir".
    await page.getByRole("button", { name: "2 satırı kaydet" }).click();
    await expect(page.getByText("2 yeni, 0 güncellenen kalem.")).toBeVisible();
    await page.getByRole("button", { name: "MOCK-001-1 (en az 10) fiyatını değiştir" }).click();
    await expect(page.getByLabel("1. satır en az miktar")).toHaveAttribute("readonly", "");
    await page.getByLabel("1. satır birim fiyat").fill("88,8");
    await page.getByRole("button", { name: "1 satırı kaydet" }).click();
    await expect(page.getByText("0 yeni, 1 güncellenen kalem.")).toBeVisible();
  });

  test("ayarlar: merge patch + If-Match; 412 → yeniden yükle", async ({ page }) => {
    const { catalog, commerce } = setup();
    const api = await signedIn(page, { catalog, commerce });
    await page.goto(`/fiyat-listeleri/${PRICE_LIST_IDS.bayiA}`);
    await page.getByLabel("Geçerlilik bitişi").fill("2026-12-31");
    await page.getByRole("button", { name: "Ayarları kaydet" }).click();
    await expect(page.getByText("Liste kaydedildi")).toBeVisible();
    const patch = requestsTo(api, "PATCH", `/api/v1/price-lists/${PRICE_LIST_IDS.bayiA}`)[0];
    expect(patch.body).toEqual({ validTo: "2026-12-31" });
    expect(patch.headers["if-match"]).toBe('W/"1"');

    commerce.priceLists.find((l) => l.id === PRICE_LIST_IDS.bayiA)!.version += 5;
    await page.getByLabel("Liste adı").fill("Bayi A Yeni (mock)");
    await page.getByRole("button", { name: "Ayarları kaydet" }).click();
    await expect(page.getByText("Liste başkası tarafından değiştirildi")).toBeVisible();
    await page.getByRole("button", { name: "Yeniden yükle" }).click();
    await expect(page.getByLabel("Liste adı")).toHaveValue("Bayi A Listesi (mock)");
  });

  test("viewer: detay salt okunur, düzenleme ve kalem ekleme yok", async ({ page }) => {
    const { catalog, commerce } = setup();
    const api = await signedIn(page, { catalog, commerce, memberships: [as("viewer")] });
    await page.goto(`/fiyat-listeleri/${PRICE_LIST_IDS.perakende}`);
    await expect(page.getByRole("heading", { level: 1, name: "Perakende (mock)" })).toBeVisible();
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByRole("form", { name: "Liste ayarları" })).toHaveCount(0);
    await expect(page.getByRole("textbox", { name: "Ürün okut veya ara" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /fiyatını değiştir/ })).toHaveCount(0);
    await expect(page.getByRole("table", { name: "Perakende (mock) fiyatları" })).toContainText("₺110,00");
    expect(api.requests.filter((r) => r.method !== "GET")).toEqual([]);
  });

  test("mobil: liste kartları ve kalem kartları", async ({ page }) => {
    await page.setViewportSize(MOBILE);
    const { catalog, commerce } = setup();
    await signedIn(page, { catalog, commerce });
    await page.goto("/fiyat-listeleri");
    await expect(page.getByRole("list", { name: "Fiyat listeleri" }).getByRole("listitem")).toHaveCount(3);
    await expectNoSeriousViolations(page);
    await page.getByRole("link", { name: "Perakende (mock)" }).click();
    const cards = page.getByRole("list", { name: "Perakende (mock) fiyatları" });
    await expect(cards.getByRole("listitem")).toHaveCount(2);
    await expect(cards.getByRole("listitem").first()).toContainText("₺110,00");
    await expectNoSeriousViolations(page);
  });
});

test.describe("fiyat sorgula (mock)", () => {
  test("müşteri için kaynak/kural gösterilir; fiyatı olmayan ürün 'Fiyat yok' (0 değil)", async ({ page }) => {
    const { catalog, commerce } = setup();
    const api = await signedIn(page, { catalog, commerce, memberships: [as("sales")] });
    await page.goto("/fiyat-listeleri/sorgula");
    await page.getByRole("searchbox", { name: "Müşteri (isteğe bağlı)" }).fill("Müşteri 001");
    await page.getByRole("button", { name: "Müşteri ara" }).click();
    await page.getByRole("button", { name: /Mock Müşteri 001/ }).click();
    await expect(page.getByText("C-001")).toBeVisible();

    const scanner = page.getByRole("textbox", { name: "Ürün ekle" });
    await scanner.fill("MOCK-001-1");
    await scanner.press("Enter");
    await expect(page.getByText("MOCK-001-1 eklendi.")).toBeVisible();
    await page.getByLabel("MOCK-001-1 miktarı").fill("10");
    await scanner.fill("MOCK-003-1");
    await scanner.press("Enter");
    await expect(page.getByText("MOCK-003-1 eklendi.")).toBeVisible();
    await page.getByRole("button", { name: "Fiyatları sorgula" }).click();

    const result = page.getByRole("table", { name: "Çözümlenen fiyatlar" });
    await expect(result.getByRole("row")).toHaveCount(3);
    const priced = result.getByRole("row", { name: /MOCK-001-1/ });
    await expect(priced).toContainText("₺90,00");
    await expect(priced).toContainText("Bayi grubu listesi: Bayi A Listesi (mock)");
    await expect(priced).toContainText("kademe: en az 10");
    const missing = result.getByRole("row", { name: /MOCK-003-1/ });
    await expect(missing).toContainText("Fiyat yok");
    await expect(missing).not.toContainText("₺");
    await expect(page.getByText("Bazı ürünlerin fiyatı yok")).toBeVisible();
    await expect(page.getByText("%5", { exact: true })).toBeVisible();

    const call = requestsTo(api, "POST", "/api/v1/pricing/quote")[0];
    expect(call.body).toEqual({
      customerId: commerce.customers[0].id,
      date: null,
      currency: null,
      lines: [
        { variantId: catalog.products[0].variants[0].id, quantity: "10" },
        { variantId: catalog.products[2].variants[0].id, quantity: "1" },
      ],
    });
    await expectNoSeriousViolations(page);
  });

  test("müşterisiz sorgu varsayılan listeyi kullanır; 422 satır hatası gösterilir", async ({ page }) => {
    const { catalog, commerce } = setup();
    await signedIn(page, { catalog, commerce });
    await page.goto("/fiyat-listeleri/sorgula");
    const scanner = page.getByRole("textbox", { name: "Ürün ekle" });
    await scanner.fill("MOCK-002-1");
    await scanner.press("Enter");
    // The lookup is async: submitting before the line is added validly reports "En az bir ürün ekleyin." (a flake).
    await expect(page.getByText("MOCK-002-1 eklendi.")).toBeVisible();
    await page.getByRole("button", { name: "Fiyatları sorgula" }).click();
    const row = page.getByRole("table", { name: "Çözümlenen fiyatlar" }).getByRole("row", { name: /MOCK-002-1/ });
    await expect(row).toContainText("₺120,00");
    await expect(row).toContainText("Varsayılan liste: Perakende (mock)");

    commerce.overrides.set("POST /api/v1/pricing/quote", (route) =>
      fulfillJson(route, 422, problemBody(422, "validation.failed", { errors: { "lines[0].quantity": ["Must be greater than 0 with at most 3 decimals."] } })),
    );
    await page.getByRole("button", { name: "Fiyatları sorgula" }).click();
    await expect(page.getByText("Sorgulanamadı")).toBeVisible();
    await expect(page.getByLabel("MOCK-002-1 miktarı")).toHaveAttribute("aria-invalid", "true");
  });
});
