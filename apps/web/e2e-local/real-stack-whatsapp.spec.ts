import { expect, test } from "@playwright/test";
import { apiOk, captureApiSession, logApiErrors, newAccount, signUpAndOnboard } from "./support/real-stack";

/**
 * REAL END-TO-END (#48 web) — no mocks: Supabase Auth + BayiOS API + PostgreSQL 17 + production web build.
 * The stack runs with AI parsing switched off (Ai:Enabled=false, no key), so the API uses its deterministic rules
 * parser — no AI provider is called. CI: .github/workflows/e2e-real.yml (`npm run test:e2e:real`, file name matches
 * "real-stack").
 *
 * Paste a WhatsApp message → review (parser notice, matched line pre-filled but unconfirmed) → confirm → the
 * whatsapp_ai draft opens on the normal order screen with the price from the price list, still a draft.
 * Setup data (product, price list, customer) is created through the API with the signed-in user's own token; the
 * flow under test goes through the web UI.
 */
const stamp = `${Date.now()}`.slice(-9);
const PRODUCT = { name: `E2E Zincir Seti ${stamp}`, sku: `WA-${stamp}` };
const CUSTOMER = { code: `W-${stamp}`, name: `E2E WhatsApp Müşterisi ${stamp}` };

test("WhatsApp mesajı → inceleme → satır onayı → whatsapp_ai taslak sipariş (kural tabanlı ayrıştırıcı)", async ({ page }) => {
  test.setTimeout(180_000);
  logApiErrors(page);
  await signUpAndOnboard(page, newAccount("wa"));
  const session = await captureApiSession(page, () => page.goto("/urunler"));

  // ---- Synthetic setup through the API (the screens for these are covered by order-to-shipment).
  const units = await apiOk<{ items: { id: string; code: string }[] }>(session, "GET", "/api/v1/units");
  const unit = units.items.find((u) => u.code.toUpperCase() === "ADET") ?? units.items[0];
  await apiOk(session, "POST", "/api/v1/products", {
    name: PRODUCT.name,
    description: null,
    categoryId: null,
    brandId: null,
    defaultUnitId: unit.id,
    taxRate: 20,
    status: null,
    sku: PRODUCT.sku,
    barcode: null,
    variants: null,
  });
  const variants = await apiOk<{ items: { id: string; sku: string }[] }>(session, "GET", `/api/v1/variants?sku=${encodeURIComponent(PRODUCT.sku)}`);
  const variantId = variants.items.find((v) => v.sku === PRODUCT.sku)!.id;
  const list = await apiOk<{ id: string }>(session, "POST", "/api/v1/price-lists", {
    name: `E2E WA Liste ${stamp}`,
    kind: "sales",
    currency: "TRY",
    isDefault: true,
    pricesIncludeTax: false,
    validFrom: null,
    validTo: null,
    basePriceListId: null,
    discountPercent: null,
  });
  await apiOk(session, "PUT", `/api/v1/price-lists/${list.id}/items`, { items: [{ variantId, minQuantity: 1, unitPrice: 75 }] });
  await apiOk(session, "POST", "/api/v1/customers", {
    code: CUSTOMER.code,
    name: CUSTOMER.name,
    contactName: null,
    taxNumber: null,
    taxOffice: null,
    phone: null,
    email: null,
    whatsappNumbers: null,
    dealerGroupId: null,
    priceListId: null,
    paymentTermDays: null,
    creditLimit: null,
    currency: null,
    notes: null,
  });

  // ---- Siparişler → WhatsApp siparişi: customer + pasted message.
  await page.goto("/siparisler");
  await page.getByRole("link", { name: "WhatsApp siparişi" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "WhatsApp siparişi" })).toBeVisible();
  await page.getByRole("searchbox", { name: "Müşteri (isteğe bağlı)" }).fill(CUSTOMER.code);
  await page.getByRole("button", { name: "Müşteri ara" }).click();
  await page.getByRole("button", { name: new RegExp(CUSTOMER.name) }).click();
  await page.getByRole("textbox", { name: /^WhatsApp mesajı/ }).fill(`Merhaba,\n3 adet ${PRODUCT.sku}\nTeşekkürler`);
  const submitted = page.waitForResponse((r) => r.url().endsWith("/api/v1/ai/order-parses") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Mesajı ayrıştır" }).click();
  expect((await submitted).status()).toBe(201);

  // ---- Review: rules parser (AI off), the line matched by SKU, pre-filled but not confirmed.
  await expect(page).toHaveURL(/\/siparisler\/whatsapp\/[0-9a-f-]{36}$/);
  await expect(page.getByText("Ayrıştırıcı: Kural tabanlı")).toBeVisible();
  await expect(page.getByTestId("whatsapp-message")).toContainText(`3 adet ${PRODUCT.sku}`);
  const line = page.getByRole("region", { name: "Satır 1", exact: true });
  await expect(line.getByText("Eşleşti", { exact: true })).toBeVisible();
  await expect(line).toContainText(PRODUCT.sku);
  await expect(page.getByLabel("Satır 1 miktarı")).toHaveValue("3");
  await expect(page.locator("main")).not.toContainText("₺");
  const confirm = page.getByRole("button", { name: "Taslak siparişi oluştur" });
  await expect(confirm).toBeDisabled();
  await page.getByLabel("Satır 1: önerilen ürünü onaylıyorum").check();
  await expect(confirm).toBeEnabled();

  const approved = page.waitForResponse((r) => r.url().endsWith("/approve") && r.request().method() === "POST");
  await confirm.click();
  const approveResponse = await approved;
  expect(approveResponse.status()).toBe(200);
  const orderId = ((await approveResponse.json()) as { orderId: string }).orderId;

  // ---- The draft on the normal order screen: source WhatsApp (AI), still a draft, price from the list.
  await expect(page).toHaveURL(new RegExp(`/siparisler/${orderId}$`));
  await expect(page.getByRole("definition").filter({ hasText: "WhatsApp (AI)" })).toBeVisible();
  await expect(page.getByRole("form", { name: "Taslak sipariş" })).toBeVisible();
  const order = await apiOk<{ order: { source: string; status: string }; lines: { sku: string; quantity: number | string; unitPrice: number | string | null }[] }>(
    session,
    "GET",
    `/api/v1/orders/${orderId}`,
  );
  expect(order.order).toMatchObject({ source: "whatsapp_ai", status: "draft" });
  expect(order.lines.map((l) => [l.sku, Number(l.quantity), Number(l.unitPrice)])).toEqual([[PRODUCT.sku, 3, 75]]);
});
