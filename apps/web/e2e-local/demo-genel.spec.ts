import { randomBytes } from "node:crypto";
import { expect, test } from "@playwright/test";
import { seedDemo } from "./demo/seeder.mjs";
import { stackConfig } from "./demo/stack.mjs";
import { signIn } from "./support/demo";
import { logApiErrors } from "./support/real-stack";

/**
 * DEFAULT DEMO — "Demo Toptan Ticaret" (generic wholesaler, "Sade başlangıç" template) on the REAL stack (nothing
 * mocked). The small version is seeded through the public API with its whole team: the owner invites a manager, a
 * sales rep and a warehouse worker, who sign up (Supabase Auth + Mailpit) and accept. Then the web app shows what the
 * product video records: prioritised picking tasks (an urgent one assigned to the warehouse worker), a two-stage
 * transfer on the road and one closed with a discrepancy, an open stock count, and the warehouse worker's own queue.
 */
test("genel demo: ekip, öncelikli görevler, yoldaki ve farklı transfer, açık sayım", async ({ page, browser }) => {
  test.setTimeout(300_000);
  const stamp = `${Date.now().toString(36)}${randomBytes(2).toString("hex")}`; // unique tenant/users per run
  logApiErrors(page);
  const password = () => `E2e-Demo-Genel-${randomBytes(9).toString("base64url")}`;
  const owner = { email: `e2e-demo-genel-${stamp}@example.test`, password: password(), fullName: "E2E Demo Toptan Sahibi" };
  const team = [
    { role: "manager", email: `e2e-genel-mudur-${stamp}@example.test`, password: password(), fullName: "E2E Örnek Müdür" },
    { role: "sales", email: `e2e-genel-satis-${stamp}@example.test`, password: password(), fullName: "E2E Örnek Satış" },
    { role: "warehouse_staff", email: `e2e-genel-depo-${stamp}@example.test`, password: password(), fullName: "E2E Örnek Depo Görevlisi" },
  ];
  const demo = await seedDemo({
    cfg: stackConfig(),
    demo: "genel",
    size: "small",
    owner,
    team,
    tenant: { name: `E2E Demo Toptan ${stamp}`, slug: `e2e-demo-genel-${stamp}` },
    include: { importCatalog: false, orders: true, whatsapp: false },
    log: (line) => console.log(`[seed] ${line}`),
  });
  const c = demo.counts;
  console.log(`[seed] small generic demo in ${demo.seconds.toFixed(1)} s: ${JSON.stringify(c)}`);

  // ---- The seeded state (public API).
  expect(c.warehouses).toBe(2);
  expect(c.membersByRole).toEqual({ manager: 1, owner: 1, sales: 1, warehouse_staff: 1 });
  expect(c.ordersByStatus).toMatchObject({ draft: 1, approved: 2, picking: 1, shipped: 1 });
  expect(c.activeTasksByPriority).toEqual({ high: 1, low: 1, urgent: 1 });
  expect(c.assignedActiveTasks).toBe(2); // urgent (waiting) and the partly picked one → warehouse worker
  expect(c.transfersByStatus).toEqual({ closed: 1, in_transit: 1 });
  expect(c.transfersWithDiscrepancy).toBe(1);
  expect(c.stockCountsByStatus).toEqual({ open: 1 });
  expect(demo.stockCount?.counted).toBeGreaterThan(0);
  expect(demo.stockCount!.counted).toBeLessThan(demo.stockCount!.lines);
  expect(c.criticalStock).toBe(4);

  const urgent = demo.orders.find((o, i) => demo.def.orders[i].priority === "urgent")!;
  const picking = demo.orders.find((o) => o.target === "picking")!;
  const [onTheRoad, closed] = demo.transfers;
  const staff = team.find((m) => m.role === "warehouse_staff")!;

  // ---- Owner: all tasks with priorities and assignees.
  await signIn(page, owner.email, owner.password, owner.fullName);
  await page.goto("/depo/gorevler");
  await page.getByRole("tab", { name: "Tüm görevler" }).click();
  const all = page.getByRole("list", { name: "Tüm görevler" });
  const urgentTask = all.getByRole("listitem").filter({ hasText: `Sipariş ${urgent.number}` });
  await expect(urgentTask).toHaveCount(1);
  await expect(urgentTask).toContainText("Öncelik: Acil");
  // The API has no profile name for members yet (sign-up leaves profiles.full_name empty, IUserDirectory): the
  // assignee is shown by the e-mail local part.
  await expect(urgentTask).toContainText(`Atanan: ${staff.email.split("@")[0]}`);
  await expect(urgentTask).toContainText("İstenen teslim");
  await expect(all.getByRole("listitem").filter({ hasText: `Sipariş ${picking.number}` })).toContainText("Öncelik: Yüksek");

  // ---- Transfers: one on the road (default filter: open), one closed with a discrepancy.
  await page.goto("/depo/transferler");
  await expect(page.getByTestId(`transfer-${onTheRoad.number}`)).toContainText("Yolda");
  await expect(page.getByTestId(`transfer-${onTheRoad.number}`)).toContainText("Şube Depo");
  await page.goto("/depo/transferler?durum=done");
  const closedItem = page.getByTestId(`transfer-${closed.number}`);
  await expect(closedItem).toContainText("Farkla kapatıldı");
  await expect(closedItem).toContainText("Fark var");

  // ---- Open stock count.
  await page.goto("/depo/sayimlar");
  await expect(page.getByTestId(`count-${demo.stockCount!.number}`)).toBeVisible();

  // ---- Warehouse worker: own queue, urgent first-class.
  // A second browser session (same settings as playwright.local.config.ts) so the owner stays signed in.
  const staffContext = await browser.newContext({
    baseURL: process.env.E2E_WEB_URL ?? "http://localhost:3200",
    locale: "tr-TR",
    timezoneId: "Europe/Istanbul",
    viewport: { width: 1280, height: 800 },
  });
  const staffPage = await staffContext.newPage();
  logApiErrors(staffPage);
  await staffPage.goto("/giris");
  await staffPage.getByLabel("E-posta").fill(staff.email);
  await staffPage.locator("input[name=password]").fill(staff.password);
  await staffPage.getByRole("button", { name: "Giriş yap" }).click();
  await expect(staffPage).toHaveURL(/\/depo\/gorevler$/, { timeout: 20_000 });
  await expect(staffPage.getByTestId("session-user")).toHaveText(staff.fullName);
  const mine = staffPage.getByRole("list", { name: "Görevlerim" });
  await expect(mine.getByRole("listitem").filter({ hasText: `Sipariş ${urgent.number}` })).toContainText("Öncelik: Acil");
  await expect(mine.getByRole("listitem").filter({ hasText: `Sipariş ${picking.number}` })).toContainText("Toplamaya devam et");
  await staffContext.close();
});
