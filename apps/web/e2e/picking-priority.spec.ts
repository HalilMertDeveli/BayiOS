/**
 * #98 web: auto-created pick tasks setting, manual task creation on an approved order, task priority (badge, filter,
 * change), customer name / requested delivery date on tasks, re-allocation off deactivated shelves, and shelf moves
 * for warehouse staff with "move reserved goods too".
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route — support/mock-backend.ts, mock-fulfilment.ts,
 * mock-orders.ts, mock-inventory.ts, mock-stock-documents.ts. All data is synthetic; nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { TENANT_A, USER, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog } from "./support/mock-catalog";
import { createMockCommerce, customerId } from "./support/mock-commerce";
import { SHELVES, createMockFulfilment, makeLine, makeTask, taskId, type MockFulfilment } from "./support/mock-fulfilment";
import { LOCATION_IDS, WAREHOUSE_IDS, createMockInventory } from "./support/mock-inventory";
import { createMockOrders, makeOrder, orderId } from "./support/mock-orders";
import { createMockStockDocuments } from "./support/mock-stock-documents";

test.use({ viewport: DESKTOP });

const OWNER = TENANT_A;
const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };
const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const PHONE = { width: 360, height: 740 } as const;

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

type Api = Awaited<ReturnType<typeof signedIn>>;
const posts = (api: Api, path: string) => api.requests.filter((r) => r.method === "POST" && r.path === path);

function withTasks(...tasks: ReturnType<typeof makeTask>[]): MockFulfilment {
  return createMockFulfilment({ tasks, userId: USER.id });
}

test.describe("ayar: onayda otomatik toplama görevi (mock)", () => {
  test("sahip kapatır: merge patch autoCreatePickingTask=false + If-Match", async ({ page }) => {
    const api = await signedIn(page);
    await page.goto("/ayarlar");
    const form = page.getByRole("form", { name: "Sipariş ayarları" });
    const toggle = form.getByRole("checkbox", { name: "Sipariş onaylanınca toplama görevi otomatik oluşturulsun" });
    await expect(toggle).toBeChecked();
    await toggle.uncheck();
    await expect(form.getByText(/“Toplama görevi oluştur” ile başlatır/)).toBeVisible();
    await form.getByRole("button", { name: "Ayarları kaydet" }).click();
    await expect(page.getByText("Sipariş ayarları kaydedildi")).toBeVisible();
    const patch = api.requests.filter((r) => r.method === "PATCH" && r.path === "/api/v1/tenant/settings")[0];
    expect(patch.body).toEqual({ autoCreatePickingTask: false });
    expect(patch.headers["if-match"]).toBe('W/"0"');
    expect(api.settings.autoCreatePickingTask).toBe(false);
    await expectNoSeriousViolations(page);
  });
});

test.describe("onaylı sipariş: toplama görevi oluştur (mock)", () => {
  function orderSetup() {
    const catalog = createMockCatalog({ products: 3 });
    const commerce = createMockCommerce(catalog);
    const orders = createMockOrders();
    const ctx = { catalog, commerce, orders };
    orders.orders.push(makeOrder(ctx, 1, { customerId: customerId(1), status: "approved", lines: [{ variantId: catalog.products[0].variants[0].id, quantity: 2 }] }));
    return { ...ctx, fulfilment: withTasks() };
  }

  test("görev yoksa yönetici öncelik seçip oluşturur; görev bağlantısı görünür", async ({ page }) => {
    const data = orderSetup();
    const api = await signedIn(page, data);
    await page.goto(`/siparisler/${orderId(1)}`);
    const section = page.getByRole("region", { name: "Toplama görevi" });
    await expect(section.getByText("Bu sipariş için aktif toplama görevi yok")).toBeVisible();
    const list = api.requests.find((r) => r.method === "GET" && r.path === "/api/v1/picking-tasks")!;
    expect(Object.fromEntries(new URLSearchParams(list.search))).toMatchObject({ orderId: orderId(1), type: "pick", status: "open,in_progress" });
    await expectNoSeriousViolations(page);

    await section.getByRole("combobox", { name: "Öncelik" }).selectOption({ label: "Acil" });
    await section.getByRole("button", { name: "Toplama görevi oluştur" }).click();
    await expect(page.getByText("SIP-2026-000001: toplama görevi oluşturuldu")).toBeVisible();
    const [create] = posts(api, "/api/v1/picking-tasks");
    expect(create.body).toEqual({ orderId: orderId(1), priority: "urgent" });
    expect(create.headers["idempotency-key"]).toBeTruthy();
    await expect(section.getByText("Öncelik: Acil")).toBeVisible();
    await expect(section.getByText("Havuzda")).toBeVisible();
    const created = data.fulfilment.tasks[0];
    await expect(section.getByRole("link", { name: "Görevi aç" })).toHaveAttribute("href", `/depo/gorevler/${created.id}`);
  });

  test("ikinci oluşturma 409 picking.fulfillment_in_progress: Türkçe mesaj, mevcut görev yüklenir", async ({ page }) => {
    const data = orderSetup();
    await signedIn(page, data);
    await page.goto(`/siparisler/${orderId(1)}`);
    const section = page.getByRole("region", { name: "Toplama görevi" });
    await expect(section.getByRole("button", { name: "Toplama görevi oluştur" })).toBeVisible();
    // Someone else created it meanwhile.
    data.fulfilment.tasks.push(makeTask(7, { orderId: orderId(1), orderNumber: "SIP-2026-000001", priority: "high" }));
    await section.getByRole("button", { name: "Toplama görevi oluştur" }).click();
    await expect(section.getByText("Siparişin zaten aktif bir toplama görevi veya onaylanmamış sevkiyatı var.")).toBeVisible();
    await expect(section.getByText("Öncelik: Yüksek")).toBeVisible();
  });

  test("satış (tasks.assign yok) görev oluşturma bölümünü görmez", async ({ page }) => {
    const data = orderSetup();
    const api = await signedIn(page, { memberships: [SALES], ...data });
    await page.goto(`/siparisler/${orderId(1)}`);
    await expect(page.getByRole("heading", { name: "Sipariş SIP-2026-000001" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Toplama görevi oluştur" })).toHaveCount(0);
    expect(posts(api, "/api/v1/picking-tasks")).toHaveLength(0);
  });
});

test.describe("görev önceliği ve müşteri (mock)", () => {
  test("liste: öncelik rozeti, müşteri, teslim tarihi; ?priority= filtresi; önceliği değiştir (If-Match)", async ({ page }) => {
    const fulfilment = withTasks(
      makeTask(1, { priority: "urgent", customerName: "Mock Bayi Acil", requestedDeliveryDate: "2026-11-12" }),
      makeTask(2, { priority: "low" }),
    );
    const api = await signedIn(page, { memberships: [OWNER], fulfilment });
    await page.goto("/depo/gorevler");
    await page.getByRole("tab", { name: "Tüm görevler" }).click();
    const list = page.getByRole("list", { name: "Tüm görevler" });
    const t1 = list.getByTestId(`task-${taskId(1)}`);
    await expect(t1.getByText("Öncelik: Acil")).toBeVisible();
    await expect(t1.getByText("Mock Bayi Acil")).toBeVisible();
    await expect(t1.getByText(/İstenen teslim: 12 Kas 2026/)).toBeVisible();
    await expect(list.getByTestId(`task-${taskId(2)}`).getByText("Öncelik: Düşük")).toBeVisible();
    await expectNoSeriousViolations(page);

    await page.getByRole("combobox", { name: "Öncelik" }).selectOption({ label: "Acil" });
    await expect(list.getByRole("listitem")).toHaveCount(1);
    expect(new URLSearchParams(api.requests.filter((r) => r.path === "/api/v1/picking-tasks").at(-1)!.search).get("priority")).toBe("urgent");
    await page.getByRole("combobox", { name: "Öncelik" }).selectOption({ label: "Tüm öncelikler" });
    await expect(list.getByRole("listitem")).toHaveCount(2);

    await list.getByTestId(`task-${taskId(2)}`).getByRole("button", { name: "Önceliği değiştir: Sipariş SIP-2026-000002" }).click();
    const dialog = page.getByRole("dialog", { name: "Sipariş SIP-2026-000002: öncelik" });
    await dialog.getByRole("combobox", { name: "Öncelik" }).selectOption({ label: "Yüksek" });
    await dialog.getByRole("button", { name: "Kaydet" }).click();
    await expect(dialog).toBeHidden();
    const [change] = posts(api, `/api/v1/picking-tasks/${taskId(2)}/priority`);
    expect(change.body).toEqual({ priority: "high" });
    expect(change.headers["if-match"]).toBe('W/"1"');
    await expect(list.getByTestId(`task-${taskId(2)}`).getByText("Öncelik: Yüksek")).toBeVisible();
  });

  test("personel: görevlerde öncelik ve müşteri görünür, öncelik değiştirme yok", async ({ page }) => {
    await page.setViewportSize(PHONE);
    const fulfilment = withTasks(makeTask(1, { assignedTo: USER.id, priority: "high", customerName: "Mock Bayi Yüksek" }));
    await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto("/depo/gorevler");
    const item = page.getByRole("list", { name: "Görevlerim" }).getByTestId(`task-${taskId(1)}`);
    await expect(item.getByText("Öncelik: Yüksek")).toBeVisible();
    await expect(item.getByText("Mock Bayi Yüksek")).toBeVisible();
    await expect(item.getByRole("button", { name: /Önceliği değiştir/ })).toHaveCount(0);
    await expectNoHorizontalScroll(page);
  });
});

test.describe("yeniden dağıtma (mock)", () => {
  test("pasif raf uyarısı; yönetici yeniden dağıtır, taşınan/taşınamayan miktar ve sunucu sırası", async ({ page }) => {
    // Line 2 (B-02, 2 to pick) is on a deactivated shelf; only 1 unit of free stock exists elsewhere (Z-99).
    const task = makeTask(1, {
      priority: "normal",
      lines: [makeLine(1), makeLine(2, { quantityToPick: 2, locationIsActive: false })],
    });
    const fulfilment = withTasks(task);
    fulfilment.spareShelfStock = 1;
    const api = await signedIn(page, { memberships: [OWNER], fulfilment });
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    await expect(page.getByText("1 satırın rafı pasif")).toBeVisible();
    await expect(page.getByTestId("line-MOCK-002").getByText("Raf pasif")).toBeVisible();
    const panel = page.getByRole("region", { name: "Yönetici işlemleri" });
    await expect(panel.getByRole("combobox", { name: "Yeniden dağıtılacak raf" }).locator("option")).toHaveText([
      "Pasif raflardaki tüm açık miktar",
      "A-01",
      "B-02 (pasif)",
    ]);
    await expectNoSeriousViolations(page);

    await panel.getByRole("button", { name: "Yeniden dağıt" }).click();
    await expect(panel.getByText("Kısmen yeniden dağıtıldı")).toBeVisible();
    await expect(panel.getByText(/1 birim boş stoğu olan aktif raflara taşındı/)).toBeVisible();
    await expect(panel.getByText(/1 birim taşınamadı/)).toBeVisible();
    const [call] = posts(api, `/api/v1/picking-tasks/${taskId(1)}/reallocate`);
    expect(call.body).toEqual({ fromLocationId: null });
    expect(call.headers["if-match"]).toBe('W/"1"');
    // Lines in the order the API sent them (walking order): A-01, B-02, then the new Z-99 line.
    await expect(page.getByRole("list").filter({ has: page.getByTestId("line-MOCK-001") }).locator("li > span:first-child")).toHaveText(["A-01", "B-02", "Z-99"]);

    // Priority from the detail.
    await panel.getByRole("combobox", { name: "Öncelik" }).selectOption({ label: "Acil" });
    await panel.getByRole("button", { name: "Önceliği kaydet" }).click();
    await expect(page.getByText("Öncelik: Acil").first()).toBeVisible();
    const [priority] = posts(api, `/api/v1/picking-tasks/${taskId(1)}/priority`);
    expect(priority.body).toEqual({ priority: "urgent" });
    expect(priority.headers["if-match"]).toBe('W/"2"');
  });

  test("tek raftan yeniden dağıt: fromLocationId gönderilir", async ({ page }) => {
    const fulfilment = withTasks(makeTask(1));
    const api = await signedIn(page, { memberships: [OWNER], fulfilment });
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    const panel = page.getByRole("region", { name: "Yönetici işlemleri" });
    await panel.getByRole("combobox", { name: "Yeniden dağıtılacak raf" }).selectOption({ label: "A-01" });
    await panel.getByRole("button", { name: "Yeniden dağıt" }).click();
    await expect(panel.getByText("Yeniden dağıtıldı", { exact: true })).toBeVisible();
    expect(posts(api, `/api/v1/picking-tasks/${taskId(1)}/reallocate`)[0].body).toEqual({ fromLocationId: SHELVES.a });
  });

  test("personel 360 px: pasif raf uyarısı başka raftan okutmayı anlatır; yönetici paneli yok", async ({ page }) => {
    await page.setViewportSize(PHONE);
    const fulfilment = withTasks(makeTask(1, { assignedTo: USER.id, lines: [makeLine(1, { locationIsActive: false })] }));
    await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    await expect(page.getByText(/başka bir raftan okutun/)).toBeVisible();
    await expect(page.getByRole("region", { name: "Yönetici işlemleri" })).toHaveCount(0);
    await expectNoHorizontalScroll(page);
    await expectNoSeriousViolations(page);
  });
});

test.describe("raf taşıma: depo personeli ve rezerve ürün (mock)", () => {
  function stockSetup() {
    const catalog = createMockCatalog({ products: 3 });
    const inventory = createMockInventory(catalog);
    const variant = catalog.products[0].variants[0];
    inventory.balances.push({ id: "00000000-0000-4000-8000-9c0000000099", variantId: variant.id, warehouseId: WAREHOUSE_IDS.raf, locationId: LOCATION_IDS.a01, onHand: 8, reserved: 2, available: 6, version: 1, updatedAt: null });
    return { catalog, inventory, stockDocuments: createMockStockDocuments(), variant };
  }

  test("personel (inventory.relocate) ayrılmış ürünü de taşır; rezervasyon taşındığı yazılır", async ({ page }) => {
    await page.setViewportSize(PHONE);
    const { variant, ...data } = stockSetup();
    const api = await signedIn(page, { memberships: [STAFF], ...data });
    await page.goto(`/stok/raf-tasima?warehouseId=${WAREHOUSE_IDS.raf}&variantId=${variant.id}&fromLocationId=${LOCATION_IDS.a01}`);
    await expect(page.getByRole("heading", { name: "Raf taşıma", level: 1 })).toBeVisible();
    await expect(page.getByText("Seçili ürün: MOCK-001-1")).toBeVisible();
    await page.getByRole("combobox", { name: "Hedef raf" }).selectOption({ label: "A-02" });
    await page.getByRole("textbox", { name: "Miktar" }).fill("7");
    await page.getByRole("button", { name: "Taşı", exact: true }).click();
    await expect(page.getByText(/Bu rafta satılabilir yalnızca 6/)).toBeVisible();
    expect(posts(api, "/api/v1/inventory/relocations")).toHaveLength(0);

    await page.getByRole("checkbox", { name: "Ayrılmış (rezerve) ürünleri de taşı" }).check();
    await expect(page.getByText("En fazla 8 (satılabilir 6 + ayrılmış 2)")).toBeVisible();
    await page.getByRole("button", { name: "Taşı", exact: true }).click();
    await expect(page.getByText("MOCK-001-1: 7 birim A-01 rafından A-02 rafına taşındı.")).toBeVisible();
    await expect(page.getByText(/Bunun 1 birimi 1 siparişe ayrılmıştı; rezervasyon ve açık toplama görevleri A-02 rafına/)).toBeVisible();
    expect(posts(api, "/api/v1/inventory/relocations")[0].body).toEqual({
      warehouseId: WAREHOUSE_IDS.raf,
      reference: null,
      lines: [{ variantId: variant.id, fromLocationId: LOCATION_IDS.a01, toLocationId: LOCATION_IDS.a02, quantity: 7, includeReserved: true }],
    });
    await expectNoHorizontalScroll(page);
    await expectNoSeriousViolations(page);
  });

  test("409 yetersiz (rezerve dahil): Türkçe açıklama ve eksik satırları", async ({ page }) => {
    const { variant, ...data } = stockSetup();
    data.stockDocuments.overrides.set("POST /api/v1/inventory/relocations", async (route) => {
      await route.fulfill({
        status: 409,
        contentType: "application/problem+json",
        headers: { "Access-Control-Allow-Origin": "*" },
        body: JSON.stringify({
          type: "about:blank",
          title: "Conflict",
          status: 409,
          code: "inventory.insufficient_available",
          shortages: [{ variantId: variant.id, sku: "MOCK-001-1", warehouseId: WAREHOUSE_IDS.raf, locationId: LOCATION_IDS.a01, requested: 8, available: 7 }],
        }),
      });
    });
    await signedIn(page, { memberships: [OWNER], ...data });
    await page.goto(`/stok/raf-tasima?warehouseId=${WAREHOUSE_IDS.raf}&variantId=${variant.id}&fromLocationId=${LOCATION_IDS.a01}`);
    await expect(page.getByText("Seçili ürün: MOCK-001-1")).toBeVisible();
    await page.getByRole("checkbox", { name: "Ayrılmış (rezerve) ürünleri de taşı" }).check();
    await page.getByRole("combobox", { name: "Hedef raf" }).selectOption({ label: "A-02" });
    await page.getByRole("textbox", { name: "Miktar" }).fill("8");
    await page.getByRole("button", { name: "Taşı", exact: true }).click();
    const alert = page.getByRole("alert").filter({ hasText: "Taşıma yapılamadı" });
    await expect(alert).toContainText("Toplanmış, paketlenmiş veya eksik bildirilmiş ürün taşınmaz");
    await expect(alert.getByRole("list", { name: "Yetersiz stok satırları" })).toContainText("MOCK-001-1 (raf A-01): istenen 8, satılabilir 7");
  });

  test("satış (inventory.relocate yok): raf taşıma kilitli", async ({ page }) => {
    const { catalog, inventory, stockDocuments } = stockSetup();
    await signedIn(page, { memberships: [SALES], catalog, inventory, stockDocuments });
    await page.goto("/stok/raf-tasima");
    await expect(page.getByRole("heading", { name: "Raf taşıma yetkiniz yok" })).toBeVisible();
  });
});
