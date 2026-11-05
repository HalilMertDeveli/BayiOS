/**
 * Warehouse picking, packing and shipment confirmation (/depo/gorevler, /depo/sevkiyatlar) — #37/#38 web.
 * MOCK: Supabase Auth and the BayiOS API (incl. the in-memory fulfilment API) are faked with page.route —
 * support/mock-backend.ts, support/mock-fulfilment.ts. All data is synthetic; nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { TENANT_A, USER, signedIn, type MockMembership } from "./support/mock-backend";
import { problemBody, fulfillJson } from "./support/mock-catalog";
import {
  OTHER_USER,
  createMockFulfilment,
  fulfilmentBusyResponse,
  makeLine,
  makeShipment,
  makeTask,
  taskId,
  type MockFulfilment,
} from "./support/mock-fulfilment";

const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };
const VIEWER: MockMembership = { ...TENANT_A, role: "viewer" };
const OWNER = TENANT_A;
const PHONE = { width: 360, height: 740 } as const;

test.use({ viewport: PHONE });

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
const posts = (api: Api, suffix: string) => api.requests.filter((r) => r.method === "POST" && r.path.endsWith(suffix));
const scanBox = (page: Page) => page.getByRole("textbox", { name: /Raf barkodu|Ürün barkodu veya SKU/ });
const feedback = (page: Page) => page.getByTestId("scan-feedback");
async function scan(page: Page, code: string) {
  const box = scanBox(page);
  await expect(box).toBeFocused();
  await box.fill(code);
  await box.press("Enter");
}

function withTasks(...tasks: ReturnType<typeof makeTask>[]): MockFulfilment {
  return createMockFulfilment({ tasks, userId: USER.id });
}

test.describe("görev listesi (mock)", () => {
  test("depo personeli girişte Görevlerim'e düşer; menüde Görevler ve Sevkiyatlar var", async ({ page }) => {
    await signedIn(page, { memberships: [STAFF], fulfilment: withTasks(makeTask(1, { assignedTo: USER.id }), makeTask(2)) });
    await page.goto("/pano");
    await expect(page).toHaveURL(/\/depo\/gorevler$/);
    await expect(page.getByRole("tab", { name: "Görevlerim" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("list", { name: "Görevlerim" }).getByRole("listitem")).toHaveCount(1);
    await expect(page.getByRole("link", { name: "Toplamaya başla" })).toBeVisible();
    await page.getByRole("tab", { name: "Havuz" }).click();
    await expect(page.getByRole("list", { name: "Havuz" }).getByRole("button", { name: "Üstlen" })).toBeVisible();
    await page.getByRole("button", { name: "Menüyü aç" }).click();
    const nav = page.getByRole("dialog", { name: "Menü" }).getByRole("navigation");
    await expect(nav.getByRole("link", { name: "Görevler" })).toHaveAttribute("aria-current", "page");
    await expect(nav.getByRole("link", { name: "Depo" })).not.toHaveAttribute("aria-current", "page");
    await expect(nav.getByRole("link", { name: "Sevkiyatlar" })).toBeVisible();
  });

  test("üstlenme yarışı: 409 picking.already_assigned anlaşılır mesaj verir ve listeyi yeniler", async ({ page }) => {
    const fulfilment = withTasks(makeTask(1), makeTask(2));
    const api = await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto("/depo/gorevler");
    await page.getByRole("tab", { name: "Havuz" }).click();
    // Someone else wins the race on task 1.
    fulfilment.overrides.set(`POST /api/v1/picking-tasks/${taskId(1)}/claim`, (route) => {
      fulfilment.tasks[0].assignedTo = OTHER_USER;
      return fulfillJson(route, 409, problemBody(409, "picking.already_assigned"), { "Access-Control-Allow-Origin": "*" });
    });
    const pool = page.getByRole("list", { name: "Havuz" });
    await pool.getByTestId(`task-${taskId(1)}`).getByRole("button", { name: "Üstlen" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Görev başkası tarafından alındı" })).toBeVisible();
    await expect(page.getByText("Bu görevi az önce başka biri aldı")).toBeVisible();
    await expect(pool.getByTestId(`task-${taskId(1)}`)).toHaveCount(0);
    expect(posts(api, "/claim")[0].headers["if-match"]).toBeUndefined();
    expect(posts(api, "/claim")[0].headers["idempotency-key"]).toBeTruthy();

    // The other one is still free: claiming opens the pick screen.
    await pool.getByTestId(`task-${taskId(2)}`).getByRole("button", { name: "Üstlen" }).click();
    await expect(page).toHaveURL(new RegExp(`/depo/gorevler/${taskId(2)}$`));
    await expect(page.getByTestId("pick-step")).toHaveText("1. Rafı okutun: A-01");
  });

  test("yönetici: tüm görevler, bana ata (If-Match) ve havuza bırak", async ({ page }) => {
    const fulfilment = withTasks(makeTask(1), makeTask(2, { assignedTo: OTHER_USER, status: "in_progress" }));
    const api = await signedIn(page, { memberships: [OWNER], fulfilment });
    await page.setViewportSize(DESKTOP);
    await page.goto("/depo/gorevler");
    await page.getByRole("tab", { name: "Tüm görevler" }).click();
    const list = page.getByRole("list", { name: "Tüm görevler" });
    await expect(list.getByRole("listitem")).toHaveCount(2);
    expect(new URLSearchParams(api.requests.filter((r) => r.path === "/api/v1/picking-tasks").at(-1)!.search).get("status")).toBe("open,in_progress");
    // owner has tasks.assign but not tasks.claim.
    const t1 = list.getByTestId(`task-${taskId(1)}`);
    await expect(t1.getByRole("button", { name: "Üstlen" })).toHaveCount(0);
    await t1.getByRole("button", { name: "Bana ata" }).click();
    await expect(t1.getByText("Bana atandı")).toBeVisible();
    const assign = posts(api, "/assign")[0];
    expect(assign.body).toEqual({ userId: USER.id });
    expect(assign.headers["if-match"]).toBe('W/"1"');

    const t2 = list.getByTestId(`task-${taskId(2)}`);
    await expect(t2.getByText("Başka personele atandı")).toBeVisible();
    await t2.getByRole("button", { name: "Havuza bırak" }).click();
    await expect(t2.getByText("Havuzda (atanmamış)")).toBeVisible();

    // Assign to a staff member by user id.
    await t2.getByRole("button", { name: "Personele ata" }).click();
    const dialog = page.getByRole("dialog", { name: /görevini ata/ });
    await dialog.getByLabel("Kime").selectOption("other");
    await dialog.getByLabel("Personelin kullanıcı kimliği").fill("geçersiz");
    await dialog.getByRole("button", { name: "Ata" }).click();
    await expect(dialog.getByText("Geçerli bir kullanıcı kimliği girin")).toBeVisible();
    await dialog.getByLabel("Personelin kullanıcı kimliği").fill(OTHER_USER);
    await dialog.getByRole("button", { name: "Ata" }).click();
    await expect(dialog).toBeHidden();
    expect(posts(api, "/assign").at(-1)!.body).toEqual({ userId: OTHER_USER });
    await expectNoSeriousViolations(page);
  });

  test("izleyici: tüm görevleri salt okunur görür, toplama ekranını açamaz, sevkiyat menüsü yok", async ({ page }) => {
    const api = await signedIn(page, { memberships: [VIEWER], fulfilment: withTasks(makeTask(1)) });
    await page.goto("/depo/gorevler");
    const list = page.getByRole("list", { name: "Tüm görevler" });
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await expect(page.getByRole("tab")).toHaveCount(0);
    await expect(list.getByRole("button")).toHaveCount(0);
    await expect(list.getByRole("link")).toHaveCount(0);
    expect(api.requests.some((r) => r.path === "/api/v1/picking-tasks/mine")).toBe(false);
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    await expect(page.getByText("Görev ayrıntısını görme yetkiniz yok")).toBeVisible();
    await page.getByRole("button", { name: "Menüyü aç" }).click();
    const nav = page.getByRole("dialog", { name: "Menü" }).getByRole("navigation");
    await expect(nav.getByRole("link", { name: "Görevler" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Sevkiyatlar" })).toHaveCount(0);
  });
});

test.describe("toplama ekranı (mock)", () => {
  test("tam akış 360 px: raf → ürün, yanlış raf / yanlış ürün / bilinmeyen kod / fazla toplama, meşgulde tek tekrar, tamamla", async ({ page }) => {
    const fulfilment = withTasks(makeTask(1, { assignedTo: USER.id }));
    const api = await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    await expect(page.getByTestId("pick-step")).toHaveText("1. Rafı okutun: A-01");
    await expect(page.getByTestId("line-MOCK-001")).toHaveAttribute("aria-current", "step");
    await expectNoHorizontalScroll(page);
    for (const name of ["Adet artır", "Eksik bildir", "Görevi tamamla"]) {
      const box = await page.getByRole("button", { name }).boundingBox();
      expect(box!.height, name).toBeGreaterThanOrEqual(48);
    }
    expect((await scanBox(page).boundingBox())!.height).toBeGreaterThanOrEqual(48);

    // Wrong shelf: an existing shelf that is not on this task → the item scan is rejected, the shelf is forgotten.
    await scan(page, "LOC-Z99");
    await expect(feedback(page)).toContainText("Raf okutuldu: LOC-Z99");
    await scan(page, "8690000000011");
    await expect(feedback(page)).toHaveAttribute("data-tone", "error");
    await expect(feedback(page)).toContainText("Yanlış raf");
    await expect(page.getByTestId("pick-step")).toHaveText("1. Rafı okutun: A-01");

    await scan(page, "LOC-A01");
    await expect(page.getByTestId("pick-step")).toHaveText("2. Ürünü okutun: MOCK-001");
    await scan(page, "MOCK-009");
    await expect(feedback(page)).toContainText("Yanlış ürün");
    await scan(page, "QQQ-404");
    await expect(feedback(page)).toContainText("Barkod tanınmadı");
    // Nothing was lost: still on the shelf, nothing counted.
    await expect(page.getByTestId("current-remaining")).toContainText("Kalan 2");
    await expect(page.getByTestId("pick-step")).toHaveText("2. Ürünü okutun: MOCK-001");

    // Busy once → one automatic retry after Retry-After with the same Idempotency-Key.
    fulfilment.overrides.set(`POST /api/v1/picking-tasks/${taskId(1)}/scans`, (route) => fulfilmentBusyResponse(route, "order.busy"));
    await scan(page, "8690000000011");
    await expect(feedback(page)).toContainText("MOCK-001 +1");
    const busyPair = posts(api, "/scans").slice(-2);
    expect(busyPair[0].headers["idempotency-key"]).toBe(busyPair[1].headers["idempotency-key"]);
    expect(busyPair[1].body).toEqual({ code: "8690000000011", location: "LOC-A01", quantity: 1, lineId: null });

    await scan(page, "8690000000011");
    await expect(feedback(page)).toContainText("Satır tamam");
    // Next line is on another shelf → asks for it.
    await expect(page.getByTestId("pick-step")).toHaveText("1. Rafı okutun: B-02");
    await expect(page.getByTestId("line-MOCK-001")).toContainText("Tamam");

    // Over-pick: back on A-01, the same item again.
    await scan(page, "LOC-A01");
    await scan(page, "8690000000011");
    await expect(feedback(page)).toContainText("Fazla toplama");

    await scan(page, "LOC-B02");
    await scan(page, "8690000000012");
    await expect(feedback(page)).toContainText("Tüm satırlar bitti");
    await page.getByRole("button", { name: "Görevi tamamla" }).click();
    await expect(page.getByText("Toplama tamamlandı", { exact: true })).toBeVisible();
    await expect(page.getByText("Tamamlandı", { exact: true })).toBeVisible();
    expect(posts(api, "/complete")[0].headers["if-match"]).toMatch(/^W\/"\d+"$/);
    await expectNoHorizontalScroll(page);
    await expectNoSeriousViolations(page);
    await page.getByRole("link", { name: "Sevkiyata git" }).click();
    await expect(page.getByRole("heading", { name: /Sevkiyat SIP-2026-000001-S1/ })).toBeVisible();
  });

  test("eksik bildirimi + eksikle tamamlama onayı", async ({ page }) => {
    const fulfilment = withTasks(makeTask(1, { assignedTo: USER.id, lines: [makeLine(1, { quantityPicked: 2 }), makeLine(2, { quantityToPick: 3 })] }));
    const api = await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    await expect(page.getByTestId("pick-step")).toHaveText("1. Rafı okutun: B-02");
    await expect(page.getByRole("button", { name: "Görevi tamamla" })).toBeDisabled();
    await page.getByRole("button", { name: "Eksik bildir" }).click();
    const dialog = page.getByRole("dialog", { name: "MOCK-002 için eksik bildir" });
    await dialog.getByRole("button", { name: "Eksik bildir" }).click();
    await expect(dialog.getByText("Bir eksik sebebi seçin.")).toBeVisible();
    await dialog.getByLabel("Sebep").selectOption("damaged");
    await expect(dialog.getByLabel("Eksik miktar (ADET)")).toHaveValue("3");
    await dialog.getByLabel("Eksik miktar (ADET)").fill("4");
    await dialog.getByRole("button", { name: "Eksik bildir" }).click();
    await expect(dialog.getByText("En fazla 3 girilebilir")).toBeVisible();
    await dialog.getByLabel("Eksik miktar (ADET)").fill("3");
    await dialog.getByRole("button", { name: "Eksik bildir" }).click();
    await expect(dialog).toBeHidden();
    expect(posts(api, "/shortage")[0].body).toEqual({ quantity: 3, reason: "damaged" });
    await expect(page.getByTestId("line-MOCK-002")).toContainText("3 eksik · Hasarlı");
    await expect(feedback(page)).toContainText("eksik bildirildi");

    await page.getByRole("button", { name: "Görevi tamamla" }).click();
    const confirm = page.getByRole("alertdialog", { name: "Eksikle tamamlansın mı?" });
    await expect(confirm).toContainText("1 satırda toplam 3 eksik var");
    await confirm.getByRole("button", { name: "Eksikle tamamla" }).click();
    await expect(page.getByText("Eksikle tamamlandı", { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Sevkiyata git" })).toBeVisible();
  });

  test("başkasına atanmış görev: salt okunur ve okutma alanı yok", async ({ page }) => {
    const fulfilment = withTasks(makeTask(1));
    await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto(`/depo/gorevler/${taskId(1)}`);
    await expect(page.getByText("Görev havuzda")).toBeVisible();
    await expect(scanBox(page)).toHaveCount(0);
    await page.getByRole("button", { name: "Üstlen ve başla" }).click();
    await expect(page.getByTestId("pick-step")).toHaveText("1. Rafı okutun: A-01");
  });
});

test.describe("sevkiyat (mock)", () => {
  function packedTask(fulfilment: MockFulfilment) {
    const task = makeTask(1, { assignedTo: USER.id, status: "completed", lines: [makeLine(1, { quantityPicked: 2 }), makeLine(2, { quantityPicked: 1, quantityToPick: 1 })] });
    fulfilment.tasks.push(task);
    fulfilment.shipments.push(makeShipment(task, 1));
    return fulfilment.shipments[0];
  }

  test("paket kontrolü (iki koli, yanlış ürün, fazla paketleme) ve personel sevk onayı", async ({ page }) => {
    const fulfilment = withTasks();
    const shipment = packedTask(fulfilment);
    const api = await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto("/depo/sevkiyatlar");
    await page.getByRole("link", { name: shipment.number }).click();
    await expect(page.getByRole("heading", { name: `Sevkiyat ${shipment.number}` })).toBeVisible();
    await expect(page.getByText("Taslak", { exact: true }).filter({ visible: true })).toBeVisible();
    // Staff cannot read orders: the order number is plain text, no link; no movement history.
    await expect(page.getByRole("link", { name: "SIP-2026-000001" })).toHaveCount(0);

    const save = page.getByRole("button", { name: /Paket kontrolünü kaydet/ });
    await expect(save).toBeDisabled();
    await scan(page, "8690000000011");
    await expect(feedback(page)).toContainText("MOCK-001 → Koli 1");
    await scan(page, "NOPE-1");
    await expect(feedback(page)).toContainText("Bu ürün sevkiyatta yok");
    await scan(page, "8690000000012");
    await scan(page, "8690000000012");
    await expect(feedback(page)).toContainText("Fazla paketleme");
    await page.getByRole("button", { name: "Yeni koli" }).click();
    await expect(page.getByText("Koli 2 / 2")).toBeVisible();
    await page.getByRole("textbox", { name: "Ürün barkodu veya SKU" }).focus();
    await scan(page, "mock-001");
    await expect(page.getByTestId("pack-line-MOCK-001")).toContainText("Tamam");
    await expectNoHorizontalScroll(page);
    await save.click();
    await expect(page.getByText("Paketlendi", { exact: true }).filter({ visible: true })).toBeVisible();
    expect(posts(api, "/pack")[0].body).toEqual({
      packages: [
        { items: [{ code: null, shipmentLineId: shipment.lines[0].id, quantity: 1 }, { code: null, shipmentLineId: shipment.lines[1].id, quantity: 1 }] },
        { items: [{ code: null, shipmentLineId: shipment.lines[0].id, quantity: 1 }] },
      ],
    });
    await expect(page.getByLabel("Koli sayısı")).toHaveValue("2");

    await page.getByLabel("Kargo firması").fill("Mock Kargo");
    await page.getByRole("button", { name: "Sevkiyatı onayla" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Sevk et" }).click();
    await expect(page.getByText("Sevk edildi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
    expect(posts(api, "/confirm")[0].body).toEqual({ carrier: "Mock Kargo", trackingNumber: null, packageCount: 2 });
    await expect(page.getByRole("heading", { name: "Stok hareketleri" })).toHaveCount(0);
    await expectNoSeriousViolations(page);
  });

  test("sevk onayı: kopan yanıt aynı anahtarla tekrar; başka cihazdan onay → already_confirmed; hareketler", async ({ page }) => {
    const fulfilment = withTasks();
    const shipment = packedTask(fulfilment);
    const api = await signedIn(page, { memberships: [OWNER], fulfilment });
    await page.goto(`/depo/sevkiyatlar/${shipment.id}`);
    await expect(page.getByRole("link", { name: "SIP-2026-000001" })).toHaveAttribute("href", /\/siparisler\//);

    // The first answer is lost on the network.
    fulfilment.overrides.set(`POST /api/v1/shipments/${shipment.id}/confirm`, (route) => route.abort("connectionreset"));
    await page.getByRole("button", { name: "Sevkiyatı onayla" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Sevk et" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Sevkiyat onaylanamadı" })).toBeVisible();
    await page.getByRole("button", { name: "Sevkiyatı onayla" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Sevk et" }).click();
    await expect(page.getByText("Sevk edildi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
    const confirms = posts(api, "/confirm");
    expect(confirms).toHaveLength(2);
    expect(confirms[1].headers["idempotency-key"]).toBe(confirms[0].headers["idempotency-key"]);
    await expect(page.getByRole("heading", { name: "Stok hareketleri" })).toBeVisible();
    await expect(page.locator("section", { has: page.getByRole("heading", { name: "Stok hareketleri" }) }).getByRole("listitem")).toHaveCount(2);
    expect(api.requests.some((r) => r.path === "/api/v1/inventory/movements" && new URLSearchParams(r.search).get("sourceId") === shipment.id)).toBe(true);
  });

  test("başka cihazda onaylanmış sevkiyat: 409 shipment.already_confirmed onaylı hâli gösterir", async ({ page }) => {
    const fulfilment = withTasks();
    const shipment = packedTask(fulfilment);
    await signedIn(page, { memberships: [OWNER], fulfilment });
    await page.goto(`/depo/sevkiyatlar/${shipment.id}`);
    await expect(page.getByRole("button", { name: "Sevkiyatı onayla" })).toBeVisible();
    // Confirmed elsewhere meanwhile (another key).
    shipment.status = "confirmed";
    shipment.shippedAt = "2026-11-05T11:00:00Z";
    await page.getByRole("button", { name: "Sevkiyatı onayla" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Sevk et" }).click();
    await expect(page.getByText("Sevkiyat zaten onaylanmış", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Sevkiyatı onayla" })).toHaveCount(0);
    await expect(page.getByText("Sevk edildi", { exact: true }).filter({ visible: true }).first()).toBeVisible();
  });

  test("paketlemede eksik: satır daha az sevk edilir", async ({ page }) => {
    const fulfilment = withTasks();
    const shipment = packedTask(fulfilment);
    const api = await signedIn(page, { memberships: [STAFF], fulfilment });
    await page.goto(`/depo/sevkiyatlar/${shipment.id}`);
    await page.getByTestId("shipment-line-MOCK-001").getByRole("button", { name: "Eksik bildir" }).click();
    const dialog = page.getByRole("dialog", { name: "MOCK-001 için eksik bildir" });
    await dialog.getByLabel("Sebep").selectOption("not_found");
    await dialog.getByLabel("Eksik miktar (ADET)").fill("1");
    await dialog.getByRole("button", { name: "Eksik bildir" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId("shipment-line-MOCK-001")).toContainText("Sevk 1 ADET");
    await expect(page.getByTestId("shipment-line-MOCK-001")).toContainText("1 eksik · Rafta bulunamadı");
    expect(posts(api, "/shortage")[0].headers["if-match"]).toBe('W/"1"');
  });
});
