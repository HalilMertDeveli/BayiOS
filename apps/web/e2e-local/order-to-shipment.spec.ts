import { expect, test, type Page } from "@playwright/test";
import { api, apiOk, captureApiSession, logApiErrors, newAccount, signUpAndOnboard, type Account, type ApiSession } from "./support/real-stack";

/**
 * REAL END-TO-END (issue #40) — no mocks: Supabase Auth (ES256) + BayiOS API + PostgreSQL 17 + production web build.
 * CI: .github/workflows/e2e-real.yml. Locally: apps/web/README.md "Gerçek yığın E2E".
 *
 * Order → reservation → picking → pack → shipment, verified on the ledger-backed stock screen, then tenant isolation
 * with a second, independently signed-up business. Every business step goes through the web UI (picking, pack check and
 * shipment confirmation on /depo/gorevler and /depo/sevkiyatlar); the API is called directly only for extra checks
 * (ledger, double-confirm, isolation) with the signed-in user's own Supabase token captured from the web app's requests.
 */
test.describe.configure({ mode: "serial" });

const stamp = `${Date.now()}`.slice(-9);
const PRODUCT = {
  name: `E2E Fren Balatası Ön ${stamp}`,
  sku: `FB-${stamp}`,
  barcode: `869${stamp}0`,
};
const CUSTOMER = { code: `C-${stamp}`, name: `E2E Motor Ticaret ${stamp}` };
const PRICE_LIST = `E2E Perakende ${stamp}`;
const RECEIVED = 10;
const ORDERED = 3;

// Shared by the serial tests below (tenant A is built once, through the UI).
let ownerA: Account;
let productId = "";
let sessionA: ApiSession;

const nav = (page: Page) => page.getByRole("navigation", { name: "Ana menü" });

async function stockRow(page: Page) {
  await page.goto("/stok");
  const table = page.getByRole("table", { name: "Stok bakiyeleri" });
  const row = table.getByRole("row").filter({ has: page.getByRole("link", { name: PRODUCT.sku, exact: true }) });
  await expect(row).toHaveCount(1);
  return row;
}

/** Rafta / Ayrılmış / Satılabilir cells of the product's stock row (Turkish number formatting). */
async function expectStock(page: Page, onHand: number, reserved: number, available: number) {
  const row = await stockRow(page);
  const cells = row.getByRole("cell");
  const header = page.getByRole("table", { name: "Stok bakiyeleri" }).getByRole("columnheader");
  const headers = (await header.allInnerTexts()).map((h) => h.trim());
  await expect(cells.nth(headers.indexOf("Rafta"))).toHaveText(String(onHand));
  await expect(cells.nth(headers.indexOf("Ayrılmış"))).toHaveText(String(reserved));
  await expect(cells.nth(headers.indexOf("Satılabilir"))).toContainText(String(available));
}

test("sipariş → rezervasyon → toplama → paket → sevkiyat; stok ledger'dan doğru düşer", async ({ page }) => {
  test.setTimeout(240_000);
  logApiErrors(page);
  ownerA = newAccount("a");
  await signUpAndOnboard(page, ownerA);

  // ---- Product with one variant (SKU + barcode); motorcycle template supplies units/fields.
  await nav(page).getByRole("link", { name: "Ürünler" }).click();
  await page.getByRole("link", { name: "Yeni ürün" }).click();
  const form = page.getByRole("form", { name: "Yeni ürün" });
  await form.getByLabel("Ürün adı").fill(PRODUCT.name);
  await form.getByLabel("SKU (stok kodu)").fill(PRODUCT.sku);
  await form.getByLabel("Barkod").fill(PRODUCT.barcode);
  const createdProduct = page.waitForResponse((r) => r.url().endsWith("/api/v1/products") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Ürünü oluştur" }).click();
  expect((await createdProduct).status()).toBe(201);
  await expect(page.getByRole("heading", { level: 1, name: PRODUCT.name })).toBeVisible();
  await expect(page).toHaveURL(/\/urunler\/[0-9a-f-]{36}$/);
  productId = new URL(page.url()).pathname.split("/").at(-1)!;
  await expect(page.getByRole("table", { name: "Varyantlar" })).toContainText(PRODUCT.sku);

  // ---- Goods receipt: scan the barcode, 10 units into the (auto-created) default warehouse.
  await page.goto("/depo/mal-kabul");
  await expect(page.getByRole("heading", { level: 1, name: "Mal kabul" })).toBeVisible();
  const scanner = page.getByLabel("Barkod veya SKU okutun");
  await scanner.fill(PRODUCT.barcode);
  await scanner.press("Enter");
  await expect(page.getByRole("status").filter({ hasText: `${PRODUCT.sku} eklendi.` })).toBeVisible();
  await page.getByLabel(`${PRODUCT.sku} miktarı`).fill(String(RECEIVED));
  await page.getByLabel("Referans (irsaliye / fatura no)").fill(`IRS-E2E-${stamp}`);
  await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
  await expect(page.getByText(`1 satır, toplam ${RECEIVED} birim stoğa girdi.`)).toBeVisible();
  await expectStock(page, RECEIVED, 0, RECEIVED);

  // ---- Default TRY sales price list with one price.
  await page.goto("/fiyat-listeleri");
  await page.getByRole("button", { name: "Yeni liste" }).click();
  const dialog = page.getByRole("dialog", { name: "Yeni fiyat listesi" });
  await dialog.getByLabel("Liste adı").fill(PRICE_LIST);
  // Ticked explicitly: with the box unticked the web sends isDefault:false, which POST /price-lists rejects (422,
  // "Only true is accepted") — product bug found by this suite, reported separately. Ticking it is a valid user path.
  await dialog.getByRole("checkbox", { name: "TRY için varsayılan satış listesi yap" }).check();
  await dialog.getByRole("button", { name: "Listeyi oluştur" }).click();
  await expect(page).toHaveURL(/\/fiyat-listeleri\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { level: 1, name: PRICE_LIST })).toBeVisible();
  const priceScanner = page.getByRole("textbox", { name: "Ürün okut veya ara" });
  await priceScanner.fill(PRODUCT.sku);
  await priceScanner.press("Enter");
  await expect(page.getByText(`${PRODUCT.sku} eklendi; fiyatını girin.`)).toBeVisible();
  await page.getByLabel("1. satır birim fiyat").fill("150");
  await page.getByRole("button", { name: "1 satırı kaydet" }).click();
  await expect(page.getByText("1 yeni, 0 güncellenen kalem.")).toBeVisible();
  await expect(page.getByRole("table", { name: `${PRICE_LIST} fiyatları` })).toContainText("₺150,00");
  await page.goto("/fiyat-listeleri");
  await expect(page.getByRole("row").filter({ hasText: PRICE_LIST })).toContainText("Varsayılan");

  // ---- Customer.
  await page.goto("/musteriler/yeni");
  await page.getByLabel("Müşteri kodu").fill(CUSTOMER.code);
  await page.getByLabel("Firma / müşteri adı").fill(CUSTOMER.name);
  await page.getByRole("button", { name: "Müşteriyi ekle" }).click();
  await expect(page).toHaveURL(/\/musteriler\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { level: 1, name: CUSTOMER.name })).toBeVisible();

  // ---- Draft order → submit → approve (all-or-nothing reservation).
  await page.goto("/siparisler/yeni");
  await page.getByRole("searchbox", { name: "Müşteri" }).fill(CUSTOMER.code);
  await page.getByRole("button", { name: "Müşteri ara" }).click();
  await page.getByRole("button", { name: new RegExp(CUSTOMER.name) }).click();
  const lineScanner = page.getByRole("textbox", { name: "Ürün ekle" });
  await lineScanner.fill(PRODUCT.barcode);
  await lineScanner.press("Enter");
  await expect(page.getByText(`${PRODUCT.sku} eklendi.`)).toBeVisible();
  await page.getByRole("textbox", { name: `${PRODUCT.sku} miktarı` }).fill(String(ORDERED));
  await page.getByRole("button", { name: "Taslak oluştur" }).click();
  await expect(page).toHaveURL(/\/siparisler\/[0-9a-f-]{36}$/);
  const orderId = new URL(page.url()).pathname.split("/").at(-1)!;
  const draft = page.getByRole("form", { name: "Taslak sipariş" }).getByTestId("draft-line-0");
  await expect(draft).toContainText("₺150,00"); // resolved from the default price list by the API
  // 3 × 150 = 450 + 20 % KDV = 540.
  await expect(page.getByTestId("order-grand-total")).toHaveText("₺540,00");
  const orderNumber = ((await page.getByRole("heading", { level: 1 }).innerText()).match(/Sipariş (\S+)/) ?? [])[1];
  expect(orderNumber).toBeTruthy();

  await page.getByRole("button", { name: "Onaya gönder" }).click();
  await expect(page.getByText(`${orderNumber}: Sipariş onaya gönderildi`)).toBeVisible();
  await page.getByRole("button", { name: "Onayla ve stok ayır" }).click();
  await expect(page.getByText(`${orderNumber}: Sipariş onaylandı; stok ayrıldı`)).toBeVisible();
  await expect(page.getByRole("list", { name: "Durum geçmişi" })).toContainText("Onaylandı, stok rezerve edildi");

  // Reservation: on hand unchanged, reserved = ordered, available reduced.
  await expectStock(page, RECEIVED, ORDERED, RECEIVED - ORDERED);

  // ---- Picking in the web app (/depo/gorevler). The owner is a supervisor (tasks.assign, no tasks.claim):
  // takes the pool task with "Bana ata", then picks by barcode (keyboard-wedge: code + Enter).
  sessionA = await captureApiSession(page, () => page.goto("/depo/gorevler"));
  await page.getByRole("tab", { name: "Tüm görevler" }).click();
  const taskItem = page.getByRole("list", { name: "Tüm görevler" }).getByRole("listitem").filter({ hasText: `Sipariş ${orderNumber}` });
  await expect(taskItem, "approval creates exactly one pick task").toHaveCount(1);
  await taskItem.getByRole("button", { name: "Bana ata" }).click();
  await expect(taskItem.getByText("Bana atandı")).toBeVisible();
  await taskItem.getByRole("link", { name: "Toplamaya başla" }).click();
  await expect(page).toHaveURL(/\/depo\/gorevler\/[0-9a-f-]{36}$/);
  // Default warehouse "Ana Depo" has no shelves: straight to the product.
  await expect(page.getByTestId("pick-step")).toHaveText(`Ürünü okutun: ${PRODUCT.sku}`);
  const pickBox = page.getByRole("textbox", { name: /Raf barkodu|Ürün barkodu veya SKU/ });
  for (let i = 1; i <= ORDERED; i++) {
    await expect(pickBox).toBeFocused();
    await pickBox.fill(PRODUCT.barcode);
    await pickBox.press("Enter");
    await expect(page.getByTestId("scan-feedback")).toContainText(i < ORDERED ? `${PRODUCT.sku} +1` : "Tüm satırlar bitti");
  }
  await page.getByRole("button", { name: "Görevi tamamla" }).click();
  await expect(page.getByText("Toplama tamamlandı", { exact: true })).toBeVisible();

  // ---- Pack check: every picked unit scanned again into a package, then confirm the shipment.
  await page.getByRole("link", { name: "Sevkiyata git" }).click();
  await expect(page).toHaveURL(/\/depo\/sevkiyatlar\/[0-9a-f-]{36}$/);
  const shipmentId = new URL(page.url()).pathname.split("/").at(-1)!;
  await expect(page.getByRole("heading", { level: 1, name: new RegExp(`Sevkiyat ${orderNumber}`) })).toBeVisible();
  const packBox = page.getByRole("textbox", { name: "Ürün barkodu veya SKU" });
  for (let i = 1; i <= ORDERED; i++) {
    await packBox.fill(PRODUCT.barcode);
    await packBox.press("Enter");
    await expect(page.getByTestId("scan-feedback")).toContainText(`${PRODUCT.sku} → Koli 1`);
  }
  await expect(page.getByTestId(`pack-line-${PRODUCT.sku}`)).toContainText("Tamam");
  await page.getByRole("button", { name: /Paket kontrolünü kaydet/ }).click();
  await expect(page.getByText("Paketlendi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
  await expect(page.getByLabel("Koli sayısı")).toHaveValue("1");
  await page.getByLabel("Kargo firması").fill("E2E Kargo");
  await page.getByRole("button", { name: "Sevkiyatı onayla" }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Sevk et" }).click();
  await expect(page.getByText("Sevk edildi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
  // Owner sees the ledger movements of the shipment: exactly one shipment_out.
  const movementsSection = page.locator("section", { has: page.getByRole("heading", { name: "Stok hareketleri" }) });
  await expect(movementsSection.getByRole("listitem")).toHaveCount(1);

  // A second confirmation (new attempt, already shipped) must not move stock twice.
  const again = await api(sessionA, "POST", `/api/v1/shipments/${shipmentId}/confirm`, {});
  expect(again.status).toBe(409);

  // ---- Back in the web app: order shipped, stock left the shelf and the reservation is consumed.
  await page.goto(`/siparisler/${orderId}`);
  await expect(page.getByRole("heading", { level: 1, name: `Sipariş ${orderNumber}` })).toBeVisible();
  await expect(page.getByText("Sevk Edildi").first()).toBeVisible();
  await expect(page.getByText("Tamamı sevk edildi").first()).toBeVisible();
  await expectStock(page, RECEIVED - ORDERED, 0, RECEIVED - ORDERED);

  const movements = await apiOk<{ items: { type: string; quantity: number | string }[] }>(
    sessionA,
    "GET",
    `/api/v1/inventory/movements?sourceId=${shipmentId}`,
  );
  expect(movements.items.map((m) => [m.type, Number(m.quantity)])).toEqual([["shipment_out", -ORDERED]]);
  await page.screenshot({ path: "test-results/real-stack-order-shipped.png", fullPage: true });
});

test("ikinci işletme birincinin ürününü göremez (UI + API, gerçek RLS)", async ({ page }) => {
  test.setTimeout(120_000);
  expect(productId, "the first test created tenant A's product").not.toBe("");
  logApiErrors(page);
  const ownerB = newAccount("b");
  await signUpAndOnboard(page, ownerB);

  // UI: B's catalog search finds nothing; A's product page is "not found".
  await page.goto(`/urunler?q=${encodeURIComponent(PRODUCT.sku)}`);
  await expect(page.getByRole("heading", { level: 1, name: "Ürünler" })).toBeVisible();
  await expect(page.getByText(PRODUCT.sku)).toHaveCount(0);
  await expect(page.getByText(PRODUCT.name)).toHaveCount(0);
  await page.goto(`/urunler/${productId}`);
  await expect(page.getByText(/bulunamadı/i).first()).toBeVisible();
  await expect(page.getByText(PRODUCT.name)).toHaveCount(0);

  await page.goto("/stok");
  await expect(page.getByRole("link", { name: PRODUCT.sku, exact: true })).toHaveCount(0);

  // API with B's own token: A's product is 404, lookups return nothing; claiming A's tenant is 403.
  const sessionB = await captureApiSession(page, () => page.goto("/urunler"));
  expect(sessionB.tenantId).not.toBe(sessionA.tenantId);
  expect((await api(sessionB, "GET", `/api/v1/products/${productId}`)).status).toBe(404);
  expect((await api(sessionB, "GET", `/api/v1/variants/lookup?barcode=${encodeURIComponent(PRODUCT.barcode)}`)).status).toBe(404);
  const variantsB = await apiOk<{ items: unknown[] }>(sessionB, "GET", `/api/v1/variants?sku=${encodeURIComponent(PRODUCT.sku)}`);
  expect(variantsB.items).toEqual([]);
  expect((await api(sessionA, "GET", `/api/v1/variants/lookup?barcode=${encodeURIComponent(PRODUCT.barcode)}`)).status).toBe(200);
  const products = await apiOk<{ items: { id: string }[] }>(sessionB, "GET", `/api/v1/products?q=${encodeURIComponent(PRODUCT.sku)}`);
  expect(products.items).toEqual([]);
  const forged = await api<{ code?: string }>({ ...sessionB, tenantId: sessionA.tenantId }, "GET", `/api/v1/products/${productId}`);
  expect(forged.status).toBe(403);
  expect(forged.body.code).toBe("tenant.access_denied");

  // A still sees it (positive control: the 404 above is isolation, not a missing row).
  expect((await api(sessionA, "GET", `/api/v1/products/${productId}`)).status).toBe(200);
});
