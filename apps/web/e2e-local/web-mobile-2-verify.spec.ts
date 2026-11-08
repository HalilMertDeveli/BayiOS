import { expect, test } from "@playwright/test";
import { Api, ensureUser, stackConfig } from "./demo/stack.mjs";
import { expectStock, packAndShip, packUnits, signIn } from "./support/demo";
import { readState } from "./support/mobile-flow";
import { logApiErrors } from "./support/real-stack";

/**
 * WEB ↔ MOBILE REAL FLOW (#45), phase 3 of 3 — after the staff user picked and received on mobile
 * (apps/mobile/integration_test/web_flow_test.dart). Real stack, no mocks; run by apps/mobile/tool/e2e.sh.
 *
 * The owner sees on the web what happened on the phone: the task is completed by the staff user, the order is packed,
 * the stock is still reserved (physical issue happens at shipment), the goods receipt is on C's shelf. The API ledger
 * agrees (movements created by the staff user). The staff user's permission split is checked with his own token
 * (receives and picks — done on mobile — but cannot adjust stock or see prices). Finally the owner packs and ships on
 * the web: stock leaves the shelves and the reservation is consumed.
 */
interface Movement {
  type: string;
  quantity: number | string;
  locationId: string | null;
  createdBy: string | null;
}

test("web↔mobil 3/3: mobil toplama ve mal kabul web'de görünür; paket + sevk; personel düzeltme yapamaz", async ({ page }) => {
  test.setTimeout(240_000);
  logApiErrors(page);
  const state = readState();
  const { A, B, C } = state.products;
  const cfg = stackConfig();
  const api = new Api(cfg, await ensureUser(cfg, state.owner), state.tenantId);
  const staffApi = new Api(cfg, await ensureUser(cfg, state.staff), state.tenantId);
  const staffId = (await staffApi.get<{ userId: string }>("/api/v1/me")).userId;

  // ---- API: the task was claimed and completed by the staff user, every unit picked, a draft shipment exists.
  const task = (await api.get<{ items: { id: string; status: string; assignedTo: string | null }[] }>(`/api/v1/picking-tasks?orderId=${state.orderId}`)).items;
  expect(task.map((t) => [t.status, t.assignedTo])).toEqual([["completed", staffId]]);
  const detail = await api.get<{ lines: { sku: string; quantityPicked: number | string; quantityShort: number | string }[]; shipmentId: string | null }>(
    `/api/v1/picking-tasks/${task[0].id}`,
  );
  expect(detail.lines.map((l) => [l.sku, Number(l.quantityPicked), Number(l.quantityShort)]).sort()).toEqual([
    [A.sku, A.ordered, 0],
    [B.sku, B.ordered, 0],
  ]);
  expect(detail.shipmentId).toBeTruthy();

  // Ledger: the mobile goods receipt is one receipt movement by the staff user, on C's shelf; picking moved nothing.
  const movements = async (variantId: string) =>
    (await api.get<{ items: Movement[] }>(`/api/v1/inventory/movements?variantId=${variantId}`)).items.map((m) => [m.type, Number(m.quantity), m.locationId, m.createdBy]);
  expect(await movements(C.variantId)).toEqual([["receipt", state.received, C.locationId, staffId]]);
  expect((await movements(A.variantId)).map(([type]) => type)).toEqual(["receipt"]);

  // ---- Staff permission split on the real API (roles-permissions §3.4/§3.5): no inventory.adjust, no prices.
  const adjust = await staffApi.raw<{ code?: string }>("POST", "/api/v1/inventory/adjustments", {
    body: { variantId: C.variantId, warehouseId: state.warehouseId, locationId: C.locationId, type: "adjustment_out", quantity: 1, reason: "E2E: personel düzeltme denemesi", confirmNegativeStock: false },
    expect: [403],
  });
  expect(adjust.body.code).toBe("auth.forbidden");
  const staffOrder = await staffApi.get<{ order: { number: string; totals: unknown }; lines: { unitPrice: unknown }[] }>(`/api/v1/orders/${state.orderId}`);
  expect(staffOrder.order.number).toBe(state.orderNumber);
  expect(staffOrder.order.totals).toBeNull();
  expect(staffOrder.lines.map((l) => l.unitPrice)).toEqual([null, null]);
  // The rejected adjustment changed nothing.
  expect(await movements(C.variantId)).toHaveLength(1);

  // ---- Web (owner): task completed by the staff user, order packed, stock still reserved, receipt on C's shelf.
  await signIn(page, state.owner.email, state.owner.password, state.owner.fullName);
  await page.goto("/depo/gorevler");
  await page.getByRole("tab", { name: "Tüm görevler" }).click();
  await page.getByLabel("Durum").selectOption("done");
  const item = page.getByRole("list", { name: "Tüm görevler" }).getByRole("listitem").filter({ hasText: `Sipariş ${state.orderNumber}` });
  await expect(item).toHaveCount(1);
  await expect(item).toContainText("Tamamlandı");
  // Display name: profile full_name, else the e-mail's local part (IUserDirectory; sign-up leaves full_name empty).
  await expect(item).toContainText(`Atanan: ${state.staff.email.split("@")[0]}`);

  await page.goto(`/siparisler/${state.orderId}`);
  await expect(page.getByRole("heading", { level: 1, name: `Sipariş ${state.orderNumber}` })).toBeVisible();
  await expect(page.getByText("Paketlendi").first()).toBeVisible();

  await expectStock(page, state.warehouseId, A.sku, A.initial, A.ordered, A.initial - A.ordered);
  await expectStock(page, state.warehouseId, B.sku, B.initial, B.ordered, B.initial - B.ordered);
  await expectStock(page, state.warehouseId, C.sku, state.received, 0, state.received);
  await expect(page.getByRole("table", { name: "Stok bakiyeleri" })).toContainText(C.shelf);
  await page.screenshot({ path: "test-results/real/web-mobile-after-pick.png", fullPage: true });

  // ---- Web (owner): pack check + shipment confirmation of what was picked on mobile.
  await page.goto(`/depo/sevkiyatlar/${detail.shipmentId}`);
  await expect(page.getByRole("heading", { level: 1, name: new RegExp(`Sevkiyat ${state.orderNumber}`) })).toBeVisible();
  await packUnits(page, A.sku, A.barcode, A.ordered);
  await packUnits(page, B.sku, B.barcode, B.ordered);
  await packAndShip(page, "E2E Kargo", `E2E-MOB-${state.stamp}`);

  await page.goto(`/siparisler/${state.orderId}`);
  await expect(page.getByText("Sevk Edildi").first()).toBeVisible();
  await expectStock(page, state.warehouseId, A.sku, A.initial - A.ordered, 0, A.initial - A.ordered);
  await expectStock(page, state.warehouseId, B.sku, B.initial - B.ordered, 0, B.initial - B.ordered);
  const shipped = (await api.get<{ items: Movement[] }>(`/api/v1/inventory/movements?sourceId=${detail.shipmentId}`)).items;
  expect(shipped.map((m) => [m.type, Number(m.quantity)]).sort()).toEqual([
    ["shipment_out", -B.ordered],
    ["shipment_out", -A.ordered],
  ].sort());
});
