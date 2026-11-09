/**
 * Sipariş detayı: "Kalanı kapat" (close-remaining) and "Tamamla" (complete) — #129 item 3.
 * MOCK: Supabase Auth and the BayiOS API (in-memory Orders with the order state machine) are faked with page.route —
 * support/mock-backend.ts, support/mock-orders.ts. All data is synthetic.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog, fulfillJson, problemBody, type MockCatalog } from "./support/mock-catalog";
import { createMockCommerce, customerId, type MockCommerce } from "./support/mock-commerce";
import { createMockOrders, makeOrder, orderId, type MockOrders } from "./support/mock-orders";

const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const VIEWER: MockMembership = { ...TENANT_A, role: "viewer" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

type Setup = { catalog: MockCatalog; commerce: MockCommerce; orders: MockOrders };

/** Order 1: packed, line 1 (5) shipped 3, line 2 (2) shipped 0 — a partial shipment. Order 2: shipped in full. */
function setup(): Setup {
  const catalog = createMockCatalog({ products: 3 });
  const commerce = createMockCommerce(catalog);
  const orders = createMockOrders();
  const ctx = { catalog, commerce };
  const v = (n: number) => catalog.products[n - 1].variants[0].id;
  const partial = makeOrder(ctx, 1, {
    customerId: customerId(1),
    status: "packed",
    fulfillmentStatus: "partial",
    lines: [
      { variantId: v(1), quantity: 5 },
      { variantId: v(2), quantity: 2 },
    ],
  });
  partial.lines[0].shippedQuantity = 3;
  partial.lines[0].reservedQuantity = 2;
  partial.lines[0].openQuantity = 2;
  partial.lines[1].openQuantity = 2;
  const shipped = makeOrder(ctx, 2, { customerId: customerId(2), status: "shipped", fulfillmentStatus: "full", lines: [{ variantId: v(1), quantity: 1 }] });
  shipped.lines[0].shippedQuantity = 1;
  shipped.lines[0].reservedQuantity = 0;
  shipped.lines[0].openQuantity = 0;
  orders.orders.push(partial, shipped);
  return { catalog, commerce, orders };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const posts = (api: Api, n: number, action: string) => api.requests.filter((r) => r.method === "POST" && r.path === `/api/v1/orders/${orderId(n)}/${action}`);

test.describe("kalanı kapat / tamamla (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("kısmi sevk: sebep + teyit, kalan miktar listelenir; If-Match + Idempotency-Key; Sevk Edildi → Tamamla → Tamamlandı", async ({ page }) => {
    const ctx = setup();
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000001" })).toBeVisible();
    const actions = page.getByRole("region", { name: "İşlemler" });
    await expect(actions.getByRole("button")).toHaveText(["Kalanı kapat"]);
    await expect(actions).toContainText("kalan rezervasyon serbest bırakılır");

    await actions.getByRole("button", { name: "Kalanı kapat" }).click();
    const dialog = page.getByRole("alertdialog", { name: "SIP-2026-000001: kalan miktar kapatılsın mı?" });
    await expect(dialog).toContainText("ayrılan stok serbest bırakılır");
    await expect(dialog).toContainText("Sevk Edildi");
    const remaining = dialog.getByRole("list", { name: "Kapatılacak kalan miktar" });
    await expect(remaining.getByRole("listitem")).toHaveCount(2);
    await expect(remaining.getByRole("listitem").first()).toContainText("MOCK-001-1");
    await expect(remaining.getByRole("listitem").first()).toContainText("2 ADET");
    await expectNoSeriousViolations(page);

    // A reason is required ("Diğer" needs a note); nothing is sent before that.
    await dialog.getByRole("button", { name: "Kalanı kapat" }).click();
    await expect(dialog.getByText("Kalanı kapatma sebebini seçin.")).toBeVisible();
    await dialog.getByLabel("Kapatma sebebi").selectOption("Diğer");
    await dialog.getByRole("button", { name: "Kalanı kapat" }).click();
    await expect(dialog.getByText("“Diğer” için kısa bir açıklama yazın.")).toBeVisible();
    expect(posts(api, 1, "close-remaining")).toHaveLength(0);
    await dialog.getByLabel("Kapatma sebebi").selectOption("Stok tedarik edilemiyor");
    await dialog.getByLabel("Açıklama").fill("Tedarikçi üretimi durdurdu");
    await dialog.getByRole("button", { name: "Kalanı kapat" }).click();

    await expect(page.getByText("SIP-2026-000001: Kalan miktar kapatıldı; kalan rezervasyon serbest bırakıldı")).toBeVisible();
    await expect(page.getByText("Sevk Edildi").first()).toBeVisible();
    // #133: the data stays fulfillmentStatus=partial; the badge says the rest was closed, not "Kısmi sevk".
    expect(ctx.orders.orders[0].fulfillmentStatus).toBe("partial");
    await expect(page.getByText("Kalan kapatıldı", { exact: true })).toBeVisible();
    await expect(page.getByText("Kısmi sevk", { exact: true })).toHaveCount(0);
    await expect(page.getByRole("list", { name: "Durum geçmişi" })).toContainText("Kalan miktar kapatıldı");
    const [close] = posts(api, 1, "close-remaining");
    expect(close.body).toEqual({ reason: "Stok tedarik edilemiyor: Tedarikçi üretimi durdurdu" });
    expect(close.headers["if-match"]).toBe('W/"1"');
    expect(close.headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(ctx.orders.orders[0].lines.map((l) => [l.cancelledQuantity, l.reservedQuantity])).toEqual([
      [2, 0],
      [2, 0],
    ]);

    // shipped → completed.
    await expect(actions.getByRole("button")).toHaveText(["Tamamla"]);
    await actions.getByRole("button", { name: "Tamamla" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş tamamlandı")).toBeVisible();
    await expect(page.getByText("Tamamlandı").first()).toBeVisible();
    await expect(page.getByText("Kalan kapatıldı", { exact: true })).toBeVisible();
    await expect(page.getByRole("region", { name: "İşlemler" })).toHaveCount(0);
    const [complete] = posts(api, 1, "complete");
    expect(complete.headers["if-match"]).toBe('W/"2"');
    expect(complete.headers["idempotency-key"]).toBeTruthy();
  });

  test("409 invalid_transition: durum değişmiş uyarısı ve güncel hâl; 412: yeniden yükle uyarısı", async ({ page }) => {
    const ctx = setup();
    await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(2)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000002" })).toBeVisible();
    // Someone completed it meanwhile.
    ctx.orders.overrides.set(`POST /api/v1/orders/${orderId(2)}/complete`, (route) => {
      ctx.orders.orders[1].status = "completed";
      ctx.orders.orders[1].version = 5;
      return fulfillJson(route, 409, problemBody(409, "order.invalid_transition"));
    });
    await page.getByRole("button", { name: "Tamamla" }).click();
    await expect(page.getByText("Sipariş durumu değişmiş")).toBeVisible();
    await expect(page.getByText("Tamamlandı").first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Tamamla" })).toHaveCount(0);

    // 412 on close-remaining: nothing changes, the user is asked to reload.
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("button", { name: "Kalanı kapat" })).toBeVisible();
    ctx.orders.orders[0].version = 9;
    await page.getByRole("button", { name: "Kalanı kapat" }).click();
    const dialog = page.getByRole("alertdialog");
    await dialog.getByLabel("Kapatma sebebi").selectOption("Müşteri kalanı istemiyor");
    await dialog.getByRole("button", { name: "Kalanı kapat" }).click();
    await expect(page.getByText("Sipariş başkası tarafından değiştirildi")).toBeVisible();
    expect(ctx.orders.orders[0].status).toBe("packed");
    await page.getByRole("button", { name: "Yeniden yükle" }).click();
    await expect(page.getByRole("button", { name: "Kalanı kapat" })).toBeVisible();
  });

  test("satış: 'satış onaylayabilir' kuralı kapalıyken Kalanı kapat yok; Tamamla hiç yok. İzleyici: işlem yok", async ({ page }) => {
    const ctx = setup();
    await signedIn(page, { ...ctx, memberships: [SALES] });
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000001" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Kalanı kapat" })).toHaveCount(0);
    await page.goto(`/siparisler/${orderId(2)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000002" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Tamamla" })).toHaveCount(0);
  });

  test("satış: kural açıkken Kalanı kapat görünür", async ({ page }) => {
    const ctx = setup();
    await signedIn(page, { ...ctx, memberships: [SALES], rules: { salesCanApproveOrders: true } });
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("region", { name: "İşlemler" }).getByRole("button")).toHaveText(["Kalanı kapat"]);
  });

  test("izleyici: işlem yok", async ({ page }) => {
    const ctx = setup();
    await signedIn(page, { ...ctx, memberships: [VIEWER] });
    for (const n of [1, 2]) {
      await page.goto(`/siparisler/${orderId(n)}`);
      await expect(page.getByRole("heading", { level: 1 })).toContainText(`SIP-2026-00000${n}`);
      await expect(page.getByRole("region", { name: "İşlemler" })).toHaveCount(0);
    }
  });
});

test.describe("kalanı kapat — mobil (mock)", () => {
  test.use({ viewport: MOBILE });

  test("diyalog mobilde erişilebilir", async ({ page }) => {
    await signedIn(page, setup());
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Kalanı kapat" }).click();
    await expect(page.getByRole("alertdialog")).toBeVisible();
    await expectNoSeriousViolations(page);
    await page.screenshot({ path: "test-results/screens/kalani-kapat-mobile.png", fullPage: true });
  });
});
