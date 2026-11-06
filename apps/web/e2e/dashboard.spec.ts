/**
 * Pano (/pano, #129 item 5) — role-aware overview built from existing endpoints only.
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts and the in-memory module
 * mocks). All data is synthetic; nothing here talks to a real API or database.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, USER, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockAssistant, parseId } from "./support/mock-assistant";
import { createMockBilling } from "./support/mock-billing";
import { createMockCatalog } from "./support/mock-catalog";
import { createMockCommerce, customerId } from "./support/mock-commerce";
import { createMockFulfilment, makeTask } from "./support/mock-fulfilment";
import { createMockOrders, makeOrder } from "./support/mock-orders";
import { createMockReports } from "./support/mock-reports";

const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const VIEWER: MockMembership = { ...TENANT_A, role: "viewer" };
const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };
const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

/** Three orders waiting for approval (one priced), two tasks (one mine, one in the pool), one parse to review. */
function seeded(options: { pending?: number; parses?: number } = {}) {
  const catalog = createMockCatalog({ products: 3 });
  const commerce = createMockCommerce(catalog);
  const orders = createMockOrders();
  const ctx = { catalog, commerce };
  for (let n = 1; n <= (options.pending ?? 3); n++) {
    orders.orders.push(makeOrder(ctx, n, { customerId: customerId(1), status: "pending_approval", lines: [{ variantId: catalog.products[0].variants[0].id, quantity: n }] }));
  }
  orders.orders.push(makeOrder(ctx, 90, { customerId: customerId(2), status: "approved" }));
  const fulfilment = createMockFulfilment({ tasks: [makeTask(1, { assignedTo: USER.id, status: "in_progress" }), makeTask(2)], userId: USER.id });
  const assistant = createMockAssistant({
    parses: Array.from({ length: options.parses ?? 1 }, (_, i) => ({
      id: parseId(i + 1),
      status: "needs_review",
      parser: "rules",
      fallbackReason: "disabled",
      summary: { lines: 3, matched: 2, ambiguous: 1, unmatched: 0, overallConfidence: 0.8 },
      customerId: null,
      orderId: null,
      createdAt: "2026-11-05T08:30:00Z",
      createdBy: USER.id,
    })),
  });
  return { catalog, commerce, orders, fulfilment, assistant };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const tile = (page: Page, name: string) => page.getByRole("region", { name });
const tenantCalls = (api: Api) =>
  api.requests.filter((r) => r.method === "GET" && r.path !== "/api/v1/me" && r.path !== "/api/v1/meta").map((r) => r.path);

test.describe("pano — masaüstü (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("işletme sahibi: onay bekleyen, bugünkü siparişler, kritik stok, görevlerim, WhatsApp, paket; az ve paralel istek", async ({ page }) => {
    const api = await signedIn(page, { ...seeded(), billing: createMockBilling({ plan: "trial", trialDaysLeft: 12 }) });
    await page.goto("/pano");
    await expect(page.getByRole("heading", { level: 1, name: "Pano" })).toBeVisible();
    await expect(page.getByText(`Hoş geldiniz, ${USER.fullName}.`)).toBeVisible();

    const pending = tile(page, "Onay bekleyen siparişler");
    await expect(pending.getByText("sipariş onay bekliyor")).toBeVisible();
    await expect(pending.getByRole("list", { name: "Son onay bekleyenler" }).getByRole("listitem")).toHaveCount(3);
    await expect(pending.getByRole("link", { name: "SIP-2026-000003" })).toHaveAttribute("href", /\/siparisler\/.+/);
    await expect(pending.getByText(/₺/).first()).toBeVisible();

    await expect(tile(page, "Bugünkü siparişler").getByText("sipariş bugün oluşturuldu")).toBeVisible();
    // MOCK critical stock: 130 items, first page 100 → "100+".
    await expect(tile(page, "Kritik stok").getByText("100+")).toBeVisible();
    await expect(tile(page, "Görevlerim").getByText("Havuzda 1 görev bekliyor.")).toBeVisible();
    await expect(tile(page, "Görevlerim").getByRole("link", { name: /Sipariş SIP-2026-000001/ })).toBeVisible();
    await expect(tile(page, "İncelenecek WhatsApp siparişleri").getByText("mesaj incelemenizi bekliyor")).toBeVisible();
    await expect(tile(page, "Paket")).toContainText("12 gün sonra bitiyor");
    await expect(tile(page, "Paket")).toContainText("Ödeme alınmaz");
    await expect(page.getByRole("navigation", { name: "Hızlı işlemler" }).getByRole("link")).toHaveText(["Sipariş oluştur", "WhatsApp'tan sipariş", "Ürün ekle", "Mal girişi"]);

    // Exactly one request per tile (the plan comes from /me).
    const calls = tenantCalls(api).filter((p) => !p.startsWith("/api/v1/tenant"));
    expect(calls.sort()).toEqual(["/api/v1/ai/order-parses", "/api/v1/orders", "/api/v1/picking-tasks/mine", "/api/v1/reports/critical-stock", "/api/v1/reports/orders"].sort());
    const orderCall = api.requests.find((r) => r.path === "/api/v1/orders")!;
    expect(new URLSearchParams(orderCall.search).get("status")).toBe("pending_approval");

    await pending.getByRole("link", { name: "Tümünü gör" }).click();
    await expect(page).toHaveURL(/\/siparisler\?durum=pending_approval$/);
    await expect(page.getByRole("table", { name: "Siparişler" }).getByRole("row")).toHaveCount(4);
    await page.goto("/pano");
    await expect(tile(page, "Kritik stok").getByText("100+")).toBeVisible();
    await expectNoSeriousViolations(page);
  });

  test("satış: kritik stok, görev ve paket kutuları yok; sipariş özetleri var", async ({ page }) => {
    const api = await signedIn(page, { memberships: [SALES], ...seeded() });
    await page.goto("/pano");
    await expect(tile(page, "Onay bekleyen siparişler").getByText("sipariş onay bekliyor")).toBeVisible();
    await expect(tile(page, "Bugünkü siparişler").getByText("sipariş bugün oluşturuldu")).toBeVisible();
    await expect(tile(page, "İncelenecek WhatsApp siparişleri")).toBeVisible();
    for (const name of ["Kritik stok", "Görevlerim", "Paket"]) await expect(tile(page, name)).toHaveCount(0);
    expect(tenantCalls(api).some((p) => p.includes("critical-stock") || p.includes("picking-tasks"))).toBe(false);
  });

  test("izleyici: rapor tutarları API gizleyince gösterilmez; hızlı işlem yok, WhatsApp kutusu yok", async ({ page }) => {
    await signedIn(page, { memberships: [VIEWER], ...seeded(), reports: createMockReports({ hideAmounts: true }) });
    await page.goto("/pano");
    const today = tile(page, "Bugünkü siparişler");
    await expect(today.getByText("sipariş bugün oluşturuldu")).toBeVisible();
    await expect(tile(page, "Onay bekleyen siparişler").getByText("sipariş onay bekliyor")).toBeVisible();
    await expect(today.getByText(/₺/)).toHaveCount(0);
    await expect(today.getByText(/Tutar \(iptal hariç\)/)).toHaveCount(0);
    await expect(page.getByRole("navigation", { name: "Hızlı işlemler" })).toHaveCount(0);
    await expect(tile(page, "İncelenecek WhatsApp siparişleri")).toHaveCount(0);
  });

  test("operasyon müdürü: boş durumlar ve tek kutunun hatası (Tekrar dene)", async ({ page }) => {
    const empty = seeded({ pending: 0, parses: 0 });
    empty.fulfilment.tasks = [];
    const reports = createMockReports({ failNext: new Set(["critical-stock"]), empty: new Set(["orders"]) });
    await signedIn(page, { memberships: [MANAGER], ...empty, reports });
    await page.goto("/pano");
    await expect(tile(page, "Onay bekleyen siparişler").getByText("Onay bekleyen sipariş yok.")).toBeVisible();
    await expect(tile(page, "Bugünkü siparişler").getByText("Bugün henüz sipariş oluşturulmadı.")).toBeVisible();
    await expect(tile(page, "Görevlerim").getByText("Size atanmış veya havuzda bekleyen görev yok.")).toBeVisible();
    await expect(tile(page, "İncelenecek WhatsApp siparişleri").getByText("İnceleme bekleyen ayrıştırma yok.")).toBeVisible();
    const critical = tile(page, "Kritik stok");
    await expect(critical.getByRole("alert")).toBeVisible();
    await expectNoSeriousViolations(page);
    await critical.getByRole("button", { name: "Tekrar dene" }).click();
    await expect(critical.getByText("100+")).toBeVisible();
  });

  test("depo personeli panoya değil Görevlerim'e gider", async ({ page }) => {
    await signedIn(page, { memberships: [STAFF], ...seeded() });
    await page.goto("/pano");
    await expect(page).toHaveURL(/\/depo\/gorevler$/);
  });
});

test.describe("pano — mobil (mock)", () => {
  test.use({ viewport: MOBILE });

  test("tek kolon, yatay taşma yok, erişilebilir", async ({ page }) => {
    await signedIn(page, seeded());
    await page.goto("/pano");
    await expect(tile(page, "Kritik stok").getByText("100+")).toBeVisible();
    await expect(tile(page, "Görevlerim").getByText("Havuzda 1 görev bekliyor.")).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    await expectNoSeriousViolations(page);
    await page.screenshot({ path: "test-results/screens/pano-mobile.png", fullPage: true });
  });
});
