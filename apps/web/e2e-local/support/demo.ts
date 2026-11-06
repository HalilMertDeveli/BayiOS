import { expect, type Page } from "@playwright/test";

/** UI helpers shared by the pilot demo scenarios (demo-motor / demo-led). Real stack, no mocks. */

export const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** "₺1.234,50" — the web app's money format (Intl tr-TR, TRY). */
export const tryMoney = (value: number | string) =>
  new Intl.NumberFormat("tr-TR", { style: "currency", currency: "TRY", minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(Number(value));

/** Sign-in form → dashboard (one membership: the tenant is selected automatically). */
export async function signIn(page: Page, email: string, password: string, fullName: string) {
  await page.goto("/giris");
  await page.getByLabel("E-posta").fill(email);
  await page.locator("input[name=password]").fill(password);
  await page.getByRole("button", { name: "Giriş yap" }).click();
  await expect(page).toHaveURL(/\/pano$/, { timeout: 20_000 });
  await expect(page.getByTestId("session-user")).toHaveText(fullName);
}

/** Order form: customer by code, then each line by barcode (keyboard-wedge scan) with its quantity → draft. */
export async function createDraftOrder(page: Page, customer: { code: string; name: string }, lines: { sku: string; code: string; quantity: number }[]) {
  await page.goto("/siparisler/yeni");
  await page.getByRole("searchbox", { name: "Müşteri" }).fill(customer.code);
  await page.getByRole("button", { name: "Müşteri ara" }).click();
  await page.getByRole("button", { name: new RegExp(escapeRegExp(customer.name)) }).click();
  const lineScanner = page.getByRole("textbox", { name: "Ürün ekle" });
  for (const line of lines) {
    await lineScanner.fill(line.code);
    await lineScanner.press("Enter");
    await expect(page.getByText(`${line.sku} eklendi.`)).toBeVisible();
    await page.getByRole("textbox", { name: `${line.sku} miktarı` }).fill(String(line.quantity));
  }
  await page.getByRole("button", { name: "Taslak oluştur" }).click();
  await expect(page).toHaveURL(/\/siparisler\/[0-9a-f-]{36}$/);
  const orderId = new URL(page.url()).pathname.split("/").at(-1)!;
  const orderNumber = await orderNumberOnPage(page);
  return { orderId, orderNumber };
}

export async function orderNumberOnPage(page: Page) {
  const heading = page.getByRole("heading", { level: 1, name: /^Sipariş / });
  await expect(heading).toBeVisible();
  const number = ((await heading.innerText()).match(/Sipariş (\S+)/) ?? [])[1];
  expect(number, "order number in the page heading").toBeTruthy();
  return number;
}

/** Draft → "Onaya gönder" → "Onayla ve stok ayır" (all-or-nothing reservation). */
export async function submitAndApprove(page: Page, orderNumber: string) {
  await page.getByRole("button", { name: "Onaya gönder" }).click();
  await expect(page.getByText(`${orderNumber}: Sipariş onaya gönderildi`)).toBeVisible();
  await page.getByRole("button", { name: "Onayla ve stok ayır" }).click();
  await expect(page.getByText(`${orderNumber}: Sipariş onaylandı; stok ayrıldı`)).toBeVisible();
}

/** /depo/gorevler: the owner (supervisor) takes the order's pool task and opens the pick screen. */
export async function openPickTask(page: Page, orderNumber: string) {
  await page.goto("/depo/gorevler");
  await page.getByRole("tab", { name: "Tüm görevler" }).click();
  const item = page.getByRole("list", { name: "Tüm görevler" }).getByRole("listitem").filter({ hasText: `Sipariş ${orderNumber}` });
  await expect(item, "approval creates exactly one pick task").toHaveCount(1);
  await item.getByRole("button", { name: "Bana ata" }).click();
  await expect(item.getByText("Bana atandı")).toBeVisible();
  await item.getByRole("link", { name: "Toplamaya başla" }).click();
  await expect(page).toHaveURL(/\/depo\/gorevler\/[0-9a-f-]{36}$/);
}

export const pickBox = (page: Page) => page.getByRole("textbox", { name: /Raf barkodu|Ürün barkodu veya SKU/ });
export const feedback = (page: Page) => page.getByTestId("scan-feedback");

/** Keyboard-wedge scan on the pick screen: shelf first (it must be on the shelf), then the product. */
export async function scanShelf(page: Page, code: string) {
  await pickBox(page).fill(code);
  await pickBox(page).press("Enter");
  await expect(feedback(page)).toContainText(`Raf okutuldu: ${code}`);
}

/** Scans one unit at a time; every answer shows "n / total … toplandı", so each scan is confirmed before the next. */
export async function pickUnits(page: Page, sku: string, code: string, quantity: number, alreadyPicked = 0, total = quantity) {
  for (let i = 1; i <= quantity; i++) {
    await pickBox(page).fill(code);
    await pickBox(page).press("Enter");
    await expect(feedback(page)).toContainText(`${sku} +1`);
    await expect(feedback(page)).toContainText(`${alreadyPicked + i} / ${total}`);
  }
}

/** Pack check: every shipped unit scanned again into box 1 (local, then one save). */
export async function packUnits(page: Page, sku: string, code: string, quantity: number) {
  const packBox = page.getByRole("textbox", { name: "Ürün barkodu veya SKU" });
  for (let i = 1; i <= quantity; i++) {
    await packBox.fill(code);
    await packBox.press("Enter");
    await expect(feedback(page)).toContainText(`${sku} → Koli 1`);
    await expect(feedback(page)).toContainText(`${i} / ${quantity}`);
  }
  await expect(page.getByTestId(`pack-line-${sku}`)).toContainText("Tamam");
}

/** Saves the pack check and confirms the shipment (carrier + tracking), as on /depo/sevkiyatlar/[id]. */
export async function packAndShip(page: Page, carrier: string, tracking: string) {
  await page.getByRole("button", { name: /Paket kontrolünü kaydet/ }).click();
  await expect(page.getByText("Paketlendi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
  await page.getByLabel("Kargo firması").fill(carrier);
  await page.getByLabel("Takip numarası").fill(tracking);
  await page.getByRole("button", { name: "Sevkiyatı onayla" }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Sevk et" }).click();
  await expect(page.getByText("Sevk edildi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
}

/** Rafta / Ayrılmış / Satılabilir of one SKU in one warehouse on /stok (ledger-backed balances). */
export async function expectStock(page: Page, warehouseId: string, sku: string, onHand: number, reserved: number, available: number) {
  await page.goto(`/stok?warehouseId=${warehouseId}&q=${encodeURIComponent(sku)}`);
  const table = page.getByRole("table", { name: "Stok bakiyeleri" });
  const row = table.getByRole("row").filter({ has: page.getByRole("link", { name: sku, exact: true }) });
  await expect(row).toHaveCount(1);
  const headers = (await table.getByRole("columnheader").allInnerTexts()).map((h) => h.trim());
  const cells = row.getByRole("cell");
  await expect(cells.nth(headers.indexOf("Rafta"))).toHaveText(String(onHand));
  await expect(cells.nth(headers.indexOf("Ayrılmış"))).toHaveText(String(reserved));
  await expect(cells.nth(headers.indexOf("Satılabilir"))).toContainText(String(available));
}
