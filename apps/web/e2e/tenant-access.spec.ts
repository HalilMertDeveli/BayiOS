/**
 * Effective permissions from GET /me and suspended/closed businesses (#86, #69).
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts); GET /me answers with the
 * MOCK permission fixtures (src/test/me-fixtures.ts). Nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, TENANT_B, USER, signedIn, type MockMembership } from "./support/mock-backend";

test.use({ viewport: DESKTOP });

const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const selectTenant = (page: Page, tenantId: string) =>
  page.addInitScript(([key, value]) => window.localStorage.setItem(key, value), [`bayios.activeTenant.${USER.id}`, tenantId] as const);

test("GET /me is asked with X-Tenant-Id and the menu follows the returned permissions (warehouse staff)", async ({ page }) => {
  const api = await signedIn(page, { memberships: [STAFF] });
  await page.goto("/pano");
  // Task-only users land on their tasks (permission-based home).
  await expect(page).toHaveURL(/\/depo\/gorevler$/);
  const nav = page.getByRole("navigation", { name: "Ana menü" });
  await expect(nav.getByRole("link", { name: "Görevler" })).toBeVisible();
  await expect(nav.getByRole("link", { name: "Siparişler" })).toBeVisible(); // orders.read K⁸ (task-linked)
  await expect(nav.getByRole("link", { name: "Pano" })).toHaveCount(0);
  await expect(nav.getByRole("link", { name: "Müşteriler" })).toHaveCount(0);
  const me = api.requests.filter((r) => r.path === "/api/v1/me");
  expect(me.some((r) => r.headers["x-tenant-id"] === STAFF.tenantId)).toBe(true);
});

test("a suspended/closed business: clear notice, no data, sign-out", async ({ page }) => {
  const api = await signedIn(page, { memberships: [TENANT_A], inactiveTenants: [TENANT_A.tenantId] });
  await page.goto("/urunler");
  const notice = page.getByRole("alert", { name: "İşletme kullanılamıyor" });
  await expect(notice.getByRole("heading", { name: "Bu işletme askıya alınmış veya kapatılmış" })).toBeVisible();
  await expect(notice).toContainText(TENANT_A.tenantName);
  await expect(notice.getByRole("button", { name: "İşletme değiştir" })).toHaveCount(0);
  await expect(page.getByRole("heading", { level: 1, name: "Ürünler" })).toHaveCount(0);
  expect(api.requests.some((r) => r.path === "/api/v1/products")).toBe(false);
  await expectNoSeriousViolations(page);
  await notice.getByRole("button", { name: "Çıkış yap" }).click();
  await expect(page).toHaveURL(/\/giris/);
});

test("suspended while working (403 tenant.inactive): the page is replaced; switch to another business", async ({ page }) => {
  await selectTenant(page, TENANT_A.tenantId);
  const api = await signedIn(page, { memberships: [TENANT_A, TENANT_B] });
  await page.goto("/urunler");
  await expect(page.getByRole("heading", { level: 1, name: "Ürünler" })).toBeVisible();
  // The list's own requests must be done first: a 403 for them would replace the page before the click (race).
  // Not waitForLoadState("networkidle"): it resolves at once if the page was idle earlier (e.g. while hydrating under
  // CPU load, before these requests started) — the flake seen in CI (#136). Wait for their visible results instead:
  // the product page answered, and the filters enabled (brands, units, categories and custom fields answered).
  await expect(page.getByText(/^\d+ ürün gösteriliyor/)).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Marka" })).toBeEnabled();

  api.inactiveTenants.push(TENANT_A.tenantId);
  await page.getByRole("navigation", { name: "Ana menü" }).getByRole("link", { name: "Stok" }).click();
  const notice = page.getByRole("alert", { name: "İşletme kullanılamıyor" });
  await expect(notice.getByRole("heading", { name: "Bu işletme askıya alınmış veya kapatılmış" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 1, name: "Stok" })).toHaveCount(0);

  await notice.getByRole("button", { name: "İşletme değiştir" }).click();
  await expect(page).toHaveURL(/\/isletme-sec$/);
  await page.getByRole("button", { name: TENANT_B.tenantName }).click();
  // TENANT_B is a warehouse_staff membership: its own permissions are loaded for it.
  await expect(page).toHaveURL(/\/depo\/gorevler$/);
  expect(api.requests.some((r) => r.path === "/api/v1/me" && r.headers["x-tenant-id"] === TENANT_B.tenantId)).toBe(true);
});

test("mobile: the notice fits the phone width and stays accessible", async ({ page }) => {
  await page.setViewportSize(MOBILE);
  await signedIn(page, { memberships: [TENANT_A, TENANT_B], inactiveTenants: [TENANT_A.tenantId] });
  await selectTenant(page, TENANT_A.tenantId);
  await page.goto("/pano");
  const notice = page.getByRole("alert", { name: "İşletme kullanılamıyor" });
  await expect(notice.getByRole("button", { name: "İşletme değiştir" })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await expectNoSeriousViolations(page);
});

test("picker (#111): a suspended business is greyed with its status; choosing it opens the notice", async ({ page }) => {
  await signedIn(page, { memberships: [TENANT_A, TENANT_B], inactiveTenants: [TENANT_B.tenantId] });
  await page.goto("/isletme-sec");
  const list = page.getByRole("list", { name: "İşletmeleriniz" });
  const suspended = list.getByRole("button", { name: TENANT_B.tenantName });
  await expect(suspended).toContainText("Askıya alındı");
  await expect(list.getByRole("button", { name: TENANT_A.tenantName })).not.toContainText("Askıya alındı");
  await expectNoSeriousViolations(page);
  await suspended.click();
  await expect(page.getByRole("alert", { name: "İşletme kullanılamıyor" })).toContainText(TENANT_B.tenantName);
});

test("mobile (#111): the menu of a suspended business is not empty — explanation, switch and sign-out", async ({ page }) => {
  await page.setViewportSize(MOBILE);
  await signedIn(page, { memberships: [TENANT_A, TENANT_B], inactiveTenants: [TENANT_A.tenantId] });
  await selectTenant(page, TENANT_A.tenantId);
  await page.goto("/pano");
  await page.getByRole("button", { name: "Menüyü aç" }).click();
  const drawer = page.getByRole("dialog", { name: "Menü" });
  await expect(drawer).toContainText("askıya alınmış veya kapatılmış; bu işletmenin menüsü kullanılamıyor");
  await expect(drawer.getByRole("button", { name: "Çıkış yap" })).toBeVisible();
  await expectNoSeriousViolations(page);
  await drawer.getByRole("button", { name: "İşletme değiştir" }).click();
  await expect(page).toHaveURL(/\/isletme-sec$/);
});
