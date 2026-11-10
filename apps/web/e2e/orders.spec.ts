/**
 * Orders (/siparisler, /siparisler/yeni, /siparisler/[id]) — #34 web.
 * MOCK: Supabase Auth and the BayiOS API (incl. in-memory Catalog, Inventory, Customers/Pricing and Orders) are faked
 * with page.route — support/mock-backend.ts, support/mock-orders.ts. All data is synthetic; nothing here talks to a real
 * API or database.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, USER, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog, problemBody, fulfillJson, type MockCatalog } from "./support/mock-catalog";
import { createMockCommerce, customerId, type MockCommerce } from "./support/mock-commerce";
import { createMockFulfilment, makeTask } from "./support/mock-fulfilment";
import { createMockOrders, makeOrder, orderBusyResponse, orderId, type MockOrders } from "./support/mock-orders";

test.use({ viewport: DESKTOP });

const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const VIEWER: MockMembership = { ...TENANT_A, role: "viewer" };
const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const variantOf = (catalog: MockCatalog, n: number) => catalog.products[n - 1].variants[0];

type Setup = { catalog: MockCatalog; commerce: MockCommerce; orders: MockOrders };

/** Catalog with 3 products (variant 3 has no price), 3 customers, and orders built by `build`. */
function setup(build: (ctx: Setup) => void = () => undefined): Setup {
  const catalog = createMockCatalog({ products: 3 });
  const commerce = createMockCommerce(catalog);
  const orders = createMockOrders();
  const ctx = { catalog, commerce, orders };
  build(ctx);
  return ctx;
}

const add = (ctx: Setup, n: number, options: Omit<Parameters<typeof makeOrder>[2], "customerId"> & { customer?: number }) =>
  ctx.orders.orders.push(makeOrder(ctx, n, { customerId: customerId(options.customer ?? 1), ...options }));

type Api = Awaited<ReturnType<typeof signedIn>>;
const requestsTo = (api: Api, method: string, path: string) => api.requests.filter((r) => r.method === method && r.path === path);
const actionPath = (n: number, action: string) => `/api/v1/orders/${orderId(n)}/${action}`;
/** A line of the saved draft's editor (scoped: a previously visited /siparisler/yeni may stay mounted but hidden). */
const draftLine = (page: Page, i: number) => page.getByRole("form", { name: "Taslak sipariş" }).getByTestId(`draft-line-${i}`);

test.describe("sipariş listesi (mock)", () => {
  test("sayfalama: ilk 50 sipariş yeniden eskiye, 'Daha fazla yükle' imleçle devamını getirir", async ({ page }) => {
    const ctx = setup((c) => {
      for (let n = 1; n <= 60; n++) add(c, n, { lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] });
    });
    const api = await signedIn(page, ctx);
    await page.goto("/siparisler");
    const table = page.getByRole("table", { name: "Siparişler" });
    await expect(table.getByRole("row")).toHaveCount(51);
    await expect(page.getByText("50 sipariş gösteriliyor (devamı var)")).toBeVisible();
    // Newest first; customer names resolved; total formatted from the API's grandTotal (110 + 20 % VAT).
    await expect(table.getByRole("row").nth(1)).toContainText("SIP-2026-000060");
    await expect(table.getByRole("row").nth(1)).toContainText("Mock Müşteri 001");
    await expect(table.getByRole("row").nth(1)).toContainText("₺132,00");
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/orders")[0].search).get("limit")).toBe("50");

    await page.getByRole("button", { name: "Daha fazla yükle" }).click();
    await expect(table.getByRole("row")).toHaveCount(61);
    await expect(page.getByText("60 sipariş gösteriliyor", { exact: true })).toBeVisible();
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/orders").at(-1)!.search).get("cursor")).toBe("o:50");
  });

  test("filtreler: durum, kaynak, tarih, müşteri ve sipariş no araması URL'de tutulur; durum + sevk rozetleri", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { status: "draft", createdAt: "2026-11-01T08:00:00Z" });
      add(c, 2, { status: "pending_approval", createdAt: "2026-11-02T08:00:00Z", customer: 2 });
      add(c, 3, { status: "picking", fulfillmentStatus: "partial", source: "whatsapp_ai", createdAt: "2026-11-03T08:00:00Z" });
      add(c, 4, { status: "cancelled", createdAt: "2026-11-04T08:00:00Z" });
    });
    const api = await signedIn(page, ctx);
    await page.goto("/siparisler");
    const table = page.getByRole("table", { name: "Siparişler" });
    await expect(table.getByRole("row")).toHaveCount(5);
    const partialRow = table.getByRole("row", { name: /SIP-2026-000003/ });
    await expect(partialRow).toContainText("Toplanıyor");
    await expect(partialRow).toContainText("Kısmi sevk");

    await page.getByRole("combobox", { name: "Durum" }).selectOption({ label: "Onay Bekliyor" });
    await expect(page).toHaveURL(/durum=pending_approval/);
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table).toContainText("SIP-2026-000002");
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/orders").at(-1)!.search).get("status")).toBe("pending_approval");

    await page.getByRole("combobox", { name: "Durum" }).selectOption({ label: "Depoda (onaylandı → paketlendi)" });
    await expect(table.getByRole("row")).toHaveCount(2);
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/orders").at(-1)!.search).get("status")).toBe("approved,picking,packed");

    await page.getByRole("button", { name: "Aramayı ve filtreleri temizle" }).first().click();
    await page.getByRole("combobox", { name: "Kaynak" }).selectOption({ label: "WhatsApp mesajı" });
    await expect(page).toHaveURL(/kaynak=whatsapp_ai/);
    await expect(table.getByRole("row")).toHaveCount(2);

    await page.getByRole("button", { name: "Aramayı ve filtreleri temizle" }).first().click();
    await page.getByLabel("Başlangıç tarihi").fill("2026-11-02");
    await page.getByLabel("Bitiş tarihi").fill("2026-11-03");
    await expect(page).toHaveURL(/baslangic=2026-11-02/);
    await expect(page).toHaveURL(/bitis=2026-11-03/);
    await expect(table.getByRole("row")).toHaveCount(3);
    const dated = new URLSearchParams(requestsTo(api, "GET", "/api/v1/orders").at(-1)!.search);
    expect([dated.get("from"), dated.get("to")]).toEqual(["2026-11-02", "2026-11-03"]);

    await page.getByRole("button", { name: "Aramayı ve filtreleri temizle" }).first().click();
    await page.getByRole("searchbox", { name: "Müşteriye göre süz" }).fill("002");
    await page.getByRole("button", { name: "Müşteri ara" }).click();
    await page.getByRole("button", { name: /Mock Müşteri 002/ }).click();
    await expect(page).toHaveURL(new RegExp(`musteri=${customerId(2)}`));
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(page.getByText("Müşteri filtresi")).toBeVisible();

    await page.getByRole("button", { name: "Aramayı ve filtreleri temizle" }).first().click();
    await page.getByRole("searchbox", { name: "Sipariş no ara" }).fill("000004");
    await expect(page).toHaveURL(/q=000004/);
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table).toContainText("İptal");
  });

  test("boş sonuç ve hata durumu", async ({ page }) => {
    const ctx = setup();
    const api = await signedIn(page, ctx);
    ctx.orders.overrides.set("GET /api/v1/orders", (route) => fulfillJson(route, 500, problemBody(500, "server.error"), { "Access-Control-Allow-Origin": "*" }));
    await page.goto("/siparisler");
    await expect(page.getByRole("heading", { name: "Siparişler yüklenemedi" })).toBeVisible();
    await page.getByRole("button", { name: "Tekrar dene" }).click();
    await expect(page.getByRole("heading", { name: "Henüz sipariş yok" })).toBeVisible();
    expect(requestsTo(api, "GET", "/api/v1/orders").length).toBeGreaterThanOrEqual(2);
  });
});

test.describe("taslak oluşturma ve düzenleme (mock)", () => {
  test("yeni taslak: müşteri + satırlar; fiyatsız satır onaya göndermeyi engeller, manuel fiyatla açılır", async ({ page }) => {
    const ctx = setup();
    const api = await signedIn(page, ctx);
    await page.goto("/siparisler");
    await page.getByRole("link", { name: "Yeni sipariş" }).click();
    await expect(page).toHaveURL(/\/siparisler\/yeni$/);

    await page.getByRole("button", { name: "Taslak oluştur" }).click();
    await expect(page.getByText("Müşteri seçin.")).toBeVisible();

    await page.getByRole("searchbox", { name: "Müşteri" }).fill("Mock");
    await page.getByRole("button", { name: "Müşteri ara" }).click();
    await page.getByRole("button", { name: /Mock Müşteri 001/ }).click();
    const scanner = page.getByRole("textbox", { name: "Ürün ekle" });
    await scanner.fill("MOCK-001-1");
    await scanner.press("Enter");
    await expect(page.getByText("MOCK-001-1 eklendi.")).toBeVisible();
    await scanner.fill("MOCK-003-1");
    await scanner.press("Enter");
    await expect(page.getByText("MOCK-003-1 eklendi.")).toBeVisible();
    await page.getByRole("textbox", { name: "MOCK-001-1 miktarı" }).fill("2,5");
    await expect(page.getByText("Kaydedince fiyatlanır")).toHaveCount(2);
    await page.getByRole("button", { name: "Taslak oluştur" }).click();

    await expect(page).toHaveURL(new RegExp(`/siparisler/${orderId(1000)}$`));
    const create = requestsTo(api, "POST", "/api/v1/orders")[0];
    expect(create.headers["idempotency-key"]).toBeTruthy();
    expect(create.body).toMatchObject({
      customerId: customerId(1),
      source: "manual",
      lines: [
        { variantId: variantOf(ctx.catalog, 1).id, quantity: "2.5", unitPrice: null },
        { variantId: variantOf(ctx.catalog, 3).id, quantity: "1", unitPrice: null },
      ],
    });

    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-001000" })).toBeVisible();
    await expect(draftLine(page, 1)).toContainText("Fiyat yok");
    await expect(draftLine(page, 0)).toContainText("₺110,00");
    await expect(page.getByText("Fiyatsız satırlar toplamlara dahil değil")).toBeVisible();
    await expect(page.getByRole("button", { name: "Onaya gönder" })).toBeDisabled();
    await expect(page.getByText("Fiyatı olmayan satırlar var; fiyatlanmadan onaya gönderilemez.")).toBeVisible();

    // Owner may override: manual price on the unpriced line, then save (PATCH with If-Match, all lines with ids).
    await page.getByRole("textbox", { name: "MOCK-003-1 manuel birim fiyat" }).fill("49,90");
    await expect(page.getByRole("button", { name: "Onaya gönder" })).toBeDisabled();
    await expect(page.getByText("Önce taslaktaki değişiklikleri kaydedin.")).toBeVisible();
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(draftLine(page, 1)).toContainText("₺49,90");
    await expect(draftLine(page, 1)).toContainText("Manuel fiyat");
    const patch = requestsTo(api, "PATCH", `/api/v1/orders/${orderId(1000)}`)[0];
    expect(patch.headers["if-match"]).toBe('W/"1"');
    expect(patch.headers["content-type"]).toBe("application/merge-patch+json");
    expect((patch.body as { lines: { id: string | null; unitPrice: string | null }[] }).lines.map((l) => [Boolean(l.id), l.unitPrice])).toEqual([
      [true, null],
      [true, "49.90"],
    ]);
    // Grand total from the API: 2.5 × 110 = 275 + 49.90 = 324.90; VAT 20 % → 389.88.
    await expect(page.getByTestId("order-grand-total")).toHaveText("₺389,88");

    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByText("SIP-2026-001000: Sipariş onaya gönderildi")).toBeVisible();
    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Taslağı kaydet" })).toHaveCount(0);
    await expect(page.getByRole("list", { name: "Durum geçmişi" })).toContainText("Onaya gönderildi");

    // Client-side back to a new order: the form starts empty again.
    await page.getByRole("link", { name: "Siparişler", exact: true }).first().click();
    await page.getByRole("link", { name: "Yeni sipariş" }).click();
    await expect(page.getByText(/Henüz satır yok/)).toBeVisible();
    await expect(page.getByRole("searchbox", { name: "Müşteri" })).toBeVisible();
  });

  test("sunucu 422 order.price_missing: fiyatsız satırlar işaretlenir", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    await signedIn(page, ctx);
    ctx.orders.overrides.set(`POST ${actionPath(1, "submit")}`, (route) =>
      fulfillJson(route, 422, problemBody(422, "order.price_missing", { errors: { "lines[0].unitPrice": ["No price for MOCK-001-1."] } }), {
        "Access-Control-Allow-Origin": "*",
      }),
    );
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Fiyatı olmayan satırlar var" })).toBeVisible();
  });

  test("412: taslak başkası tarafından değiştirildiyse yeniden yükleme uyarısı", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("textbox", { name: "MOCK-001-1 miktarı" }).fill("3");
    ctx.orders.orders[0].version = 7; // someone else saved meanwhile
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page.getByText("Sipariş başkası tarafından değiştirildi")).toBeVisible();
    await page.getByRole("button", { name: "Yeniden yükle" }).click();
    await expect(page.getByRole("textbox", { name: "MOCK-001-1 miktarı" })).toHaveValue("1");
    await expect(page.getByText("Sipariş başkası tarafından değiştirildi")).toBeHidden();
  });
});

test.describe("onay, red, iptal (mock)", () => {
  test("onay mutlu yol: If-Match + Idempotency-Key, durum Onaylandı, stok ayrıldı, depo bölümü salt okunur", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 2 }] }));
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("button", { name: "Reddet" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Onaydan geri çek" })).toBeVisible();
    await page.getByRole("button", { name: "Onayla ve stok ayır" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş onaylandı; stok ayrıldı")).toBeVisible();
    const approve = requestsTo(api, "POST", actionPath(1, "approve"))[0];
    expect(approve.headers["if-match"]).toBe('W/"1"');
    expect(approve.headers["idempotency-key"]).toBeTruthy();
    expect(approve.body ?? {}).not.toHaveProperty("confirmNegativeStock"); // OD-09: nothing to confirm

    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toBeHidden();
    await expect(page.getByRole("button", { name: "Siparişi iptal et" })).toBeVisible();
    const fulfilment = page.getByRole("region", { name: "Satır bazında sevkiyat durumu" });
    await expect(fulfilment).toContainText("MOCK-001-1");
    await expect(page.getByText(/Toplama, paketleme, sevkiyat ve kalan miktarı kapatma işlemleri Depo modülünün/)).toBeVisible();
    // No fulfilment actions on this screen (JOB 10 owns them).
    for (const name of [/toplama.*başlat/i, /paketle/i, /sevk et/i, /kalanı kapat/i]) await expect(page.getByRole("button", { name })).toHaveCount(0);
    await expect(page.getByRole("list", { name: "Durum geçmişi" })).toContainText("Onaylandı, stok rezerve edildi");
  });

  test("depoda: kısmi sevk + eksik bildirimi gösterilir; iptal yok, yalnızca “Kalanı kapat” (short-close, #129)", async ({ page }) => {
    const ctx = setup((c) =>
      add(c, 1, { status: "picking", fulfillmentStatus: "partial", hasShortage: true, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 2 }] }),
    );
    await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000001" })).toBeVisible();
    await expect(page.getByText("Kısmi sevk").first()).toBeVisible();
    await expect(page.getByText("Depoda eksik bildirildi").first()).toBeVisible();
    const actions = page.getByRole("region", { name: "İşlemler" });
    await expect(actions.getByRole("button")).toHaveText(["Kalanı kapat"]);
    await expect(page.getByRole("button", { name: "Siparişi iptal et" })).toHaveCount(0);
    await expect(page.getByRole("region", { name: "Satır bazında sevkiyat durumu" })).toContainText("MOCK-001-1");
  });

  test("stok yetersiz: ya hep ya hiç mesajı ve eksik satır listesi; sipariş onay bekliyor kalır", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 5 }, { variantId: variantOf(c.catalog, 2).id, quantity: 1 }] });
      c.orders.available.set(variantOf(c.catalog, 1).id, 2);
    });
    await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onayla ve stok ayır" }).click();
    await expect(page.getByRole("heading", { name: "Onaylanamadı: stok yetersiz" })).toBeVisible();
    await expect(page.getByText(/hiçbir satıra stok ayrılmadı/)).toBeVisible();
    const list = page.getByRole("list", { name: "Stoğu yetmeyen satırlar" });
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await expect(list).toContainText("MOCK-001-1");
    await expect(list).toContainText("istenen 5, satılabilir 2");
    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toBeVisible();
    expect(ctx.orders.orders[0].status).toBe("pending_approval");
  });

  test("stok eksiye düşmez (OD-09): teyit diyaloğu yok, tek istek, sipariş onay bekliyor kalır", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 5 }] });
      c.orders.available.set(variantOf(c.catalog, 1).id, 2);
    });
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onayla ve stok ayır" }).click();
    await expect(page.getByRole("heading", { name: "Onaylanamadı: stok yetersiz" })).toBeVisible();
    await expect(page.getByRole("alertdialog")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /Yine de/ })).toHaveCount(0);
    await expectNoSeriousViolations(page);
    expect(requestsTo(api, "POST", actionPath(1, "approve"))).toHaveLength(1);
    expect(ctx.orders.orders[0].status).toBe("pending_approval");
  });

  test("iptal: sebep zorunlu, rezervasyon serbest bırakılır, zaman çizelgesinde sebep görünür", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { status: "approved", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 2 }] }));
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Siparişi iptal et" }).click();
    const dialog = page.getByRole("alertdialog", { name: "SIP-2026-000001 iptal edilsin mi?" });
    await expect(dialog).toContainText("ayrılan stok serbest bırakılır");
    await dialog.getByRole("button", { name: "Siparişi iptal et" }).click();
    await expect(dialog.getByText("Bir iptal sebebi seçin.")).toBeVisible();
    expect(requestsTo(api, "POST", actionPath(1, "cancel"))).toHaveLength(0);
    await dialog.getByRole("combobox", { name: "İptal sebebi" }).selectOption("Müşteri vazgeçti");
    await dialog.getByRole("textbox", { name: "Açıklama" }).fill("telefonla bildirdi");
    await dialog.getByRole("button", { name: "Siparişi iptal et" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş iptal edildi")).toBeVisible();
    expect(requestsTo(api, "POST", actionPath(1, "cancel"))[0].body).toEqual({ reason: "Müşteri vazgeçti: telefonla bildirdi" });
    await expect(page.getByRole("list", { name: "Durum geçmişi" })).toContainText("Sebep: Müşteri vazgeçti: telefonla bildirdi");
    await expect(page.getByRole("button", { name: "Siparişi iptal et" })).toHaveCount(0);
  });

  test("red: sebep ile taslağa döner", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Reddet" }).click();
    const dialog = page.getByRole("alertdialog");
    await dialog.getByRole("textbox", { name: "Ret sebebi" }).fill("Fiyat yanlış");
    await dialog.getByRole("button", { name: "Reddet" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş reddedildi ve taslağa döndü")).toBeVisible();
    expect(requestsTo(api, "POST", actionPath(1, "reject"))[0].body).toEqual({ reason: "Fiyat yanlış" });
    await expect(page.getByRole("button", { name: "Taslağı kaydet" })).toBeVisible();
  });

  test("409 order.invalid_transition: güncel hâl yüklenir ve mesaj gösterilir", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toBeVisible();
    // Someone else cancelled it meanwhile.
    Object.assign(ctx.orders.orders[0], { status: "cancelled", version: 2, cancelReason: "Mükerrer sipariş", cancelledAt: "2026-11-05T11:00:00Z" });
    ctx.orders.overrides.set(`POST ${actionPath(1, "approve")}`, (route) =>
      fulfillJson(route, 409, problemBody(409, "order.invalid_transition"), { "Access-Control-Allow-Origin": "*" }),
    );
    await page.getByRole("button", { name: "Onayla ve stok ayır" }).click();
    await expect(page.getByText("Sipariş durumu değişmiş")).toBeVisible();
    await expect(page.getByText(/şu anki durumunda yapılamaz/)).toBeVisible();
    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toHaveCount(0);
    await expect(page.getByText(/Mükerrer sipariş/)).toBeVisible();
    expect(requestsTo(api, "GET", `/api/v1/orders/${orderId(1)}`).length).toBeGreaterThanOrEqual(2);
  });

  test("409 order.busy: Retry-After sonrası aynı anahtarla bir kez tekrar dener", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    const api = await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    ctx.orders.overrides.set(`POST ${actionPath(1, "submit")}`, (route) => orderBusyResponse(route));
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş onaya gönderildi")).toBeVisible();
    const [first, second] = requestsTo(api, "POST", actionPath(1, "submit"));
    expect(second.headers["idempotency-key"]).toBe(first.headers["idempotency-key"]);
  });

  test("412 geçişte: yeniden yükleme uyarısı", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    await signedIn(page, ctx);
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("button", { name: "Onaydan geri çek" })).toBeVisible();
    ctx.orders.orders[0].version = 5; // someone else changed it after it was loaded
    await page.getByRole("button", { name: "Onaydan geri çek" }).click();
    await expect(page.getByText("Sipariş başkası tarafından değiştirildi")).toBeVisible();
    await page.getByRole("button", { name: "Yeniden yükle" }).click();
    await page.getByRole("button", { name: "Onaydan geri çek" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş taslağa geri çekildi")).toBeVisible();
  });
});

test.describe("rol kısıtları (mock)", () => {
  test("satış: başkasının taslağını düzenleyemez / iptal edemez; onaylayamaz (kural kapalı); fiyat giremez; kendi taslağı açık", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] });
      add(c, 2, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] });
    });
    const api = await signedIn(page, { ...ctx, memberships: [SALES] });
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("button", { name: "Onaya gönder" })).toBeEnabled();
    await expect(page.getByRole("button", { name: "Siparişi iptal et" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Taslağı kaydet" })).toHaveCount(0);
    await expect(page.getByText(/Bu taslağı başka bir kullanıcı oluşturdu/)).toBeVisible();

    await page.goto(`/siparisler/${orderId(2)}`);
    await expect(page.getByRole("button", { name: "Onaydan geri çek" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Reddet" })).toHaveCount(0);

    await page.goto("/siparisler/yeni");
    await page.getByRole("searchbox", { name: "Müşteri" }).fill("001");
    await page.getByRole("button", { name: "Müşteri ara" }).click();
    await page.getByRole("button", { name: /Mock Müşteri 001/ }).click();
    await page.getByRole("textbox", { name: "Ürün ekle" }).fill("MOCK-002-1");
    await page.getByRole("textbox", { name: "Ürün ekle" }).press("Enter");
    await expect(page.getByRole("textbox", { name: "MOCK-002-1 miktarı" })).toBeVisible();
    await expect(page.getByRole("textbox", { name: "MOCK-002-1 manuel birim fiyat" })).toHaveCount(0);
    await expect(page.getByRole("combobox", { name: "MOCK-002-1 iskonto türü" })).toHaveCount(0);
    await page.getByRole("button", { name: "Taslak oluştur" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-001002" })).toBeVisible();
    expect((requestsTo(api, "POST", "/api/v1/orders")[0].body as { lines: { unitPrice: unknown; discountPercent: unknown }[] }).lines[0]).toMatchObject({
      unitPrice: null,
      discountPercent: null,
    });
    // The draft just created is the user's own (orders.edit.draft O, orders.cancel K¹¹): editable and cancellable.
    await expect(page.getByRole("button", { name: "Taslağı kaydet" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Siparişi iptal et" })).toBeVisible();
  });

  test("satış: kendi taslağını düzenler; API reddederse (403) açık mesaj gösterilir", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    const api = await signedIn(page, { ...ctx, memberships: [SALES] });
    await page.goto(`/siparisler/${orderId(1)}`);
    const quantity = draftLine(page, 0).getByRole("textbox", { name: "MOCK-001-1 miktarı" });
    await quantity.fill("3");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page.getByText("Taslak kaydedildi")).toBeVisible();
    const patch = requestsTo(api, "PATCH", `/api/v1/orders/${orderId(1)}`)[0].body as { lines: { quantity: string; unitPrice: unknown }[] };
    expect(patch.lines[0]).toMatchObject({ quantity: "3", unitPrice: null });
    await expect(page.getByRole("button", { name: "Siparişi iptal et" })).toBeVisible();

    // The API decides per record: a refusal (e.g. ownership changed) is shown plainly, nothing is lost.
    // MOCK 403 orders.not_owner (#111): the message comes from the code, not the title.
    ctx.orders.overrides.set(`PATCH /api/v1/orders/${orderId(1)}`, (route) =>
      fulfillJson(route, 403, problemBody(403, "orders.not_owner", { title: "x", permission: "orders.edit.draft" })),
    );
    await draftLine(page, 0).getByRole("textbox", { name: "MOCK-001-1 miktarı" }).fill("4");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Taslak kaydedilemedi" })).toContainText("yalnızca oluşturan kişi");
    await expect(draftLine(page, 0).getByRole("textbox", { name: "MOCK-001-1 miktarı" })).toHaveValue("4");
    await expectNoSeriousViolations(page);
  });

  test("satış + iskonto sınırı (K¹⁰): iskonto girer, manuel fiyat yok; sınırı aşan iskonto açık mesajla reddedilir", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    const api = await signedIn(page, { ...ctx, memberships: [SALES], rules: { salesMaxDiscountPercent: 10 } });
    await page.goto(`/siparisler/${orderId(1)}`);
    const line = draftLine(page, 0);
    await expect(line.getByRole("textbox", { name: "MOCK-001-1 manuel birim fiyat" })).toHaveCount(0);
    await line.getByRole("combobox", { name: "MOCK-001-1 iskonto türü" }).selectOption("percent");
    // The cap comes from GET /me (parameters.maxDiscountPercent, #111) and is shown next to the field.
    await expect(line.getByText("En fazla %10", { exact: true })).toBeVisible();
    await line.getByRole("textbox", { name: "MOCK-001-1 iskonto oranı (%)" }).fill("15");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(line.getByText("İskonto en fazla %10 olabilir.")).toBeVisible();
    expect(requestsTo(api, "PATCH", `/api/v1/orders/${orderId(1)}`)).toHaveLength(0);
    // The API still decides (e.g. the limit was lowered meanwhile): 403 orders.discount_limit_exceeded with limitPercent.
    ctx.orders.overrides.set(`PATCH /api/v1/orders/${orderId(1)}`, (route) =>
      fulfillJson(route, 403, problemBody(403, "orders.discount_limit_exceeded", { permission: "orders.price.override", limitPercent: 7.5 })),
    );
    await line.getByRole("textbox", { name: "MOCK-001-1 iskonto oranı (%)" }).fill("9");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Taslak kaydedilemedi" })).toContainText("İskonto en fazla %7,5 olabilir.");
    await line.getByRole("textbox", { name: "MOCK-001-1 iskonto oranı (%)" }).fill("5");
    await page.getByRole("button", { name: "Taslağı kaydet" }).click();
    await expect(page.getByText("Taslak kaydedildi")).toBeVisible();
    const patches = requestsTo(api, "PATCH", `/api/v1/orders/${orderId(1)}`);
    expect((patches.at(-1)!.body as { lines: { discountPercent: string; unitPrice: unknown }[] }).lines[0]).toMatchObject({ discountPercent: "5", unitPrice: null });
  });

  test("izleyici: salt okunur, işlem ve yeni sipariş yok", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    await signedIn(page, { ...ctx, memberships: [VIEWER] });
    await page.goto("/siparisler");
    await expect(page.getByRole("table", { name: "Siparişler" }).getByRole("row")).toHaveCount(2);
    await expect(page.getByRole("link", { name: "Yeni sipariş" })).toHaveCount(0);
    await page.getByRole("link", { name: "SIP-2026-000001" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000001" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "İşlemler" })).toHaveCount(0);
    await expect(page.getByRole("table", { name: "Sipariş satırları" })).toContainText("MOCK-001-1");
  });

  test("depo personeli: yalnızca görevlerine bağlı siparişler; fiyatlar '—', müşteri araması ve işlem yok", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { status: "approved", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 2 }] });
      add(c, 2, { status: "approved", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] });
    });
    const fulfilment = createMockFulfilment({ tasks: [makeTask(1)] });
    const api = await signedIn(page, { ...ctx, fulfilment, memberships: [STAFF] });
    await page.goto("/siparisler");
    const table = page.getByRole("table", { name: "Siparişler" });
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table).toContainText("SIP-2026-000001");
    await expect(table).not.toContainText("₺");
    await expect(page.getByRole("searchbox", { name: "Müşteriye göre süz" })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Yeni sipariş" })).toHaveCount(0);
    await table.getByRole("link", { name: "SIP-2026-000001" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-000001" })).toBeVisible();
    await expect(page.getByText(/Fiyat ve tutarlar rolünüz gereği gösterilmiyor/)).toBeVisible();
    await expect(page.getByRole("table", { name: "Sipariş satırları" })).toContainText("MOCK-001-1");
    await expect(page.getByText("Fiyat yok")).toHaveCount(0);
    await expect(page.locator("main")).not.toContainText("₺");
    await expect(page.getByRole("heading", { name: "İşlemler" })).toHaveCount(0);
    expect(api.requests.some((r) => r.path.startsWith("/api/v1/customers"))).toBe(false);
    // An order outside the user's tasks: the API answers 404.
    await page.goto(`/siparisler/${orderId(2)}`);
    await expect(page.getByText("Yalnızca size atanmış veya havuzdaki görevlere bağlı siparişleri görebilirsiniz.")).toBeVisible();
    await expectNoSeriousViolations(page);
  });
});

test.describe("sipariş ayarları etkisi ve adresler (mock, #92)", () => {
  test("onay zorunlu değil: sahibin gönderdiği sipariş otomatik onaylanır ve stok ayrılır", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 2 }] }));
    const api = await signedIn(page, { ...ctx, settings: { requireOrderApproval: false } });
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş otomatik onaylandı; stok ayrıldı")).toBeVisible();
    await expect(page.getByText(/sipariş onayı zorunlu değil/)).toBeVisible();
    await expect(page.getByRole("region", { name: "Satır bazında sevkiyat durumu" })).toContainText("MOCK-001-1");
    expect(requestsTo(api, "POST", actionPath(1, "submit"))).toHaveLength(1);
    expect(requestsTo(api, "POST", actionPath(1, "approve"))).toHaveLength(0);
    expect(ctx.orders.orders[0].status).toBe("approved");
  });

  test("onay zorunlu değil + stok yetmiyor (engelle): sipariş taslak kalır, eksik satırlar listelenir", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 5 }] });
      c.orders.available.set(variantOf(c.catalog, 1).id, 2);
    });
    await signedIn(page, { ...ctx, settings: { requireOrderApproval: false } });
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByRole("heading", { name: "Gönderilemedi: stok yetersiz" })).toBeVisible();
    await expect(page.getByText(/taslak olarak kaldı/)).toBeVisible();
    expect(ctx.orders.orders[0].status).toBe("draft");
  });

  test("onay zorunlu değil + stok yetersiz: gönderim reddedilir, teyit sorulmaz (OD-09), taslak kalır", async ({ page }) => {
    const ctx = setup((c) => {
      add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 5 }] });
      c.orders.available.set(variantOf(c.catalog, 1).id, 2);
    });
    const api = await signedIn(page, { ...ctx, settings: { requireOrderApproval: false } });
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByRole("heading", { name: "Gönderilemedi: stok yetersiz" })).toBeVisible();
    await expect(page.getByRole("alertdialog")).toHaveCount(0);
    const submits = requestsTo(api, "POST", actionPath(1, "submit"));
    expect(submits).toHaveLength(1);
    expect(submits[0].body).toBeNull();
    expect(ctx.orders.orders[0].status).toBe("draft");
  });

  test("satış (onay yetkisi yok): onay zorunlu olmasa da sipariş onay bekler", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    await signedIn(page, { ...ctx, memberships: [SALES], settings: { requireOrderApproval: false } });
    await page.goto(`/siparisler/${orderId(1)}`);
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByText("SIP-2026-000001: Sipariş onaya gönderildi")).toBeVisible();
    expect(ctx.orders.orders[0].status).toBe("pending_approval");
  });

  test("teslimat/fatura adresi: varsayılan seçili gelir, başka adres seçilir; gönderimde anlık görüntü gösterilir", async ({ page }) => {
    const ctx = setup();
    const api = await signedIn(page, ctx);
    await page.goto("/siparisler/yeni");
    await expect(page.getByText("Teslimat ve fatura adresi müşteri seçildikten sonra seçilir.")).toBeVisible();
    await page.getByRole("searchbox", { name: "Müşteri" }).fill("001");
    await page.getByRole("button", { name: "Müşteri ara" }).click();
    await page.getByRole("button", { name: /Mock Müşteri 001/ }).click();
    const shipping = page.getByRole("combobox", { name: "Teslimat adresi" });
    await expect(shipping).toBeEnabled();
    await expect(shipping.locator("option:checked")).toHaveText(/Merkez \(mock\).*\(varsayılan\)/);
    await expect(page.getByText(/fatura adresi yok; müşteri kartından ekleyebilirsiniz/)).toBeVisible();
    await shipping.selectOption({ label: "Depo (mock) — Sanayi Sit. Mock Sk. No:2, Mock İlçe / İstanbul" });
    await page.getByRole("textbox", { name: "Ürün ekle" }).fill("MOCK-001-1");
    await page.getByRole("textbox", { name: "Ürün ekle" }).press("Enter");
    await page.getByRole("button", { name: "Taslak oluştur" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Sipariş SIP-2026-001000" })).toBeVisible();
    expect(requestsTo(api, "POST", "/api/v1/orders")[0].body).toMatchObject({ shippingAddressId: ctx.commerce.addresses[1].id, billingAddressId: null });

    const addresses = page.getByRole("region", { name: "Adresler" });
    await expect(addresses).toContainText("Önizleme");
    await expect(addresses).toContainText("Sanayi Sit. Mock Sk. No:2");
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByText("SIP-2026-001000: Sipariş onaya gönderildi")).toBeVisible();
    await expect(addresses).toContainText("Sipariş gönderildiğinde kaydedilen adresler");
    await expect(addresses).toContainText("Sanayi Sit. Mock Sk. No:2");
    // A later change on the customer card does not touch the snapshot.
    ctx.commerce.addresses[1].line1 = "Değişti Cd. 9";
    await page.reload();
    await expect(page.getByRole("region", { name: "Adresler" })).toContainText("Sanayi Sit. Mock Sk. No:2");
    await expectNoSeriousViolations(page);
  });

  test("seçili adres silinmişse gönderim 422 ile açıklanır", async ({ page }) => {
    const ctx = setup((c) => add(c, 1, { createdBy: USER.id, lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }] }));
    await signedIn(page, ctx);
    ctx.orders.orders[0].shippingAddressId = "00000000-0000-4000-8000-7e0000000999";
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("combobox", { name: "Teslimat adresi" })).toHaveValue("00000000-0000-4000-8000-7e0000000999");
    await expect(page.getByText("Seçili adres müşteriden silinmiş. Başka bir adres seçin.")).toBeVisible();
    await page.getByRole("button", { name: "Onaya gönder" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Adres geçersiz" })).toContainText("Teslimat adresi: Seçili adres müşteriden silinmiş");
  });
});

test.describe("mobil ve erişilebilirlik (mock)", () => {
  test("mobil: liste kartları ve detay", async ({ page }) => {
    await page.setViewportSize(MOBILE);
    const ctx = setup((c) => add(c, 1, { status: "pending_approval", lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 2 }] }));
    await signedIn(page, ctx);
    await page.goto("/siparisler");
    const cards = page.getByRole("list", { name: "Siparişler" });
    await expect(cards.getByRole("listitem")).toHaveCount(1);
    await expect(cards).toContainText("Onay Bekliyor");
    await expect(cards).toContainText("₺264,00");
    await expectNoSeriousViolations(page);
    await cards.getByRole("link", { name: "SIP-2026-000001" }).click();
    await expect(page.getByRole("button", { name: "Onayla ve stok ayır" })).toBeVisible();
    await expect(page.getByRole("list", { name: "Sipariş satırları" })).toContainText("MOCK-001-1");
    await expectNoSeriousViolations(page);
  });

  for (const [name, viewport] of [
    ["desktop", DESKTOP],
    ["mobile", MOBILE],
  ] as const) {
    test(`axe — ${name}: liste, yeni sipariş, taslak detay`, async ({ page }) => {
      await page.setViewportSize(viewport);
      const ctx = setup((c) => add(c, 1, { lines: [{ variantId: variantOf(c.catalog, 1).id, quantity: 1 }, { variantId: variantOf(c.catalog, 3).id, quantity: 1 }] }));
      await signedIn(page, ctx);
      await page.goto("/siparisler");
      await expect(page.getByRole("heading", { level: 1, name: "Siparişler" })).toBeVisible();
      await expect(page.getByText("1 sipariş gösteriliyor")).toBeVisible();
      await expectNoSeriousViolations(page);
      await page.goto("/siparisler/yeni");
      await expect(page.getByRole("heading", { level: 1, name: "Yeni sipariş" })).toBeVisible();
      await expectNoSeriousViolations(page);
      await page.goto(`/siparisler/${orderId(1)}`);
      await expect(draftLine(page, 1)).toContainText("Fiyat yok");
      await expectNoSeriousViolations(page);
      await page.getByRole("button", { name: "Siparişi iptal et" }).click();
      await expect(page.getByRole("alertdialog")).toBeVisible();
      await expectNoSeriousViolations(page);
    });
  }
});
