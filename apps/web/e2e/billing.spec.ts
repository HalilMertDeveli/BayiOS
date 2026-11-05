/**
 * Paket ve modüller, deneme bildirimi, entitlement kapıları (#51/#52 web).
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts, support/mock-billing.ts):
 * the plan behind GET /me `entitlements`, /billing/* and the module store are in memory. No payment exists anywhere;
 * all data is synthetic. Nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockBilling } from "./support/mock-billing";
import { problemBody } from "./support/mock-catalog";
import { WAREHOUSE_IDS } from "./support/mock-inventory";

test.use({ viewport: DESKTOP });

const ADMIN: MockMembership = { ...TENANT_A, role: "admin" };
const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

test("owner on the trial: plan, trial end, usage vs limits, module store, 'ödeme yok' (MOCK)", async ({ page }) => {
  await signedIn(page);
  await page.goto("/ayarlar");
  await page.getByRole("link", { name: /Paket ve modüller/ }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Paket ve modüller" })).toBeVisible();
  await expect(page.getByText("Ücretsiz deneme — ödeme yok")).toBeVisible();
  const current = page.getByRole("region", { name: "Mevcut paket" });
  await expect(current).toContainText("Profesyonel");
  await expect(current).toContainText("Ücretsiz deneme");
  await expect(current).toContainText("25 gün kaldı");
  const usage = page.getByRole("region", { name: "Kullanım ve sınırlar" });
  await expect(usage).toContainText("Aktif depo sayısı");
  await expect(usage).toContainText("2 / sınırsız");
  await expect(usage).toContainText("Aylık AI ayrıştırma (bu ay)");
  await expect(usage).toContainText("12 / 1.000");
  const modules = page.getByRole("list", { name: "Ek modüller" });
  await expect(modules.getByRole("listitem").filter({ hasText: "AI WhatsApp asistanı" })).toContainText("Denemeye dahil");
  await expect(modules.getByRole("listitem").filter({ hasText: "Bayi portalı" })).toContainText("Yakında");
  await expect(page.getByRole("list", { name: "Paketler" }).getByRole("listitem").filter({ hasText: "Profesyonel" })).toContainText("Mevcut paketiniz");
  await expectNoSeriousViolations(page);
});

test("owner activates and deactivates an add-on (free, Idempotency-Key, confirm, /me reloaded) (MOCK)", async ({ page }) => {
  const billing = createMockBilling({ plan: "professional" });
  const api = await signedIn(page, { billing });
  await page.goto("/ayarlar/paket");
  const card = page.getByRole("list", { name: "Ek modüller" }).getByRole("listitem").filter({ hasText: "AI WhatsApp asistanı" });
  await expect(card).toContainText("Etkinleştirilebilir");
  const meCalls = () => api.requests.filter((r) => r.path === "/api/v1/me").length;
  const before = meCalls();

  await card.getByRole("button", { name: /Ücretsiz etkinleştir/ }).click();
  await expect(page.getByText("Modül etkinleştirildi")).toBeVisible();
  await expect(card).toContainText("Ücretsiz deneme");
  const activate = billing.requests.find((r) => r.method === "POST" && r.path.endsWith("/ai_assistant/activate"))!;
  expect(activate.headers["idempotency-key"]).toBeTruthy();
  await expect.poll(meCalls).toBeGreaterThan(before);

  await card.getByRole("button", { name: /Kapat/ }).click();
  const dialog = page.getByRole("alertdialog", { name: "AI WhatsApp asistanı kapatılsın mı?" });
  await expect(dialog).toContainText("mevcut verileriniz silinmez");
  await expectNoSeriousViolations(page);
  await dialog.getByRole("button", { name: "Modülü kapat" }).click();
  await expect(page.getByText("Modül kapatıldı")).toBeVisible();
  await expect(card).toContainText("Kapatıldı");
  await expect(card.getByRole("button", { name: /Ücretsiz etkinleştir/ })).toBeVisible();
});

test("409 billing.trial_used: clear message, the store is reloaded (MOCK)", async ({ page }) => {
  const billing = createMockBilling({ plan: "basic" });
  billing.modules[0] = { ...billing.modules[0], trialUsed: true };
  await signedIn(page, { billing });
  await page.goto("/ayarlar/paket");
  const card = page.getByRole("list", { name: "Ek modüller" }).getByRole("listitem").filter({ hasText: "AI WhatsApp asistanı" });
  await card.getByRole("button", { name: /Ücretsiz etkinleştir/ }).click();
  const alert = page.getByRole("alert").filter({ hasText: "Modül işlemi yapılamadı" });
  await expect(alert).toContainText("ücretsiz denemesi daha önce kullanıldı");
  await expect(alert).toContainText("ödeme alınmaz");
  await expect.poll(() => billing.requests.filter((r) => r.method === "GET" && r.path === "/api/v1/billing/modules").length).toBeGreaterThan(1);
});

test("admin reads the plan page but cannot change modules; manager has no access (MOCK)", async ({ page }) => {
  const api = await signedIn(page, { memberships: [ADMIN], billing: createMockBilling({ plan: "professional" }) });
  await page.goto("/ayarlar/paket");
  await expect(page.getByRole("region", { name: "Mevcut paket" })).toContainText("Profesyonel");
  await expect(page.getByText("Modülleri yalnızca işletme sahibi etkinleştirebilir veya kapatabilir.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: /Ücretsiz etkinleştir/ })).toHaveCount(0);

  api.memberships = [MANAGER];
  await page.goto("/ayarlar/paket");
  await expect(page.getByRole("heading", { name: "Paket bilgilerini görme yetkiniz yok" })).toBeVisible();
  await page.goto("/ayarlar");
  await expect(page.getByRole("heading", { level: 1, name: "Ayarlar" })).toBeVisible();
  await expect(page.getByRole("link", { name: /Paket ve modüller/ })).toHaveCount(0);
});

test("trial banner: shown when the trial ends within a week, dismissible for the session (MOCK)", async ({ page }) => {
  await signedIn(page, { billing: createMockBilling({ plan: "trial", trialDaysLeft: 3 }) });
  await page.goto("/pano");
  const banner = page.getByRole("region", { name: "Deneme süresi" });
  await expect(banner).toContainText("3 gün sonra bitiyor");
  await expect(banner).toContainText("Ödeme alınmaz");
  await expectNoSeriousViolations(page);
  await banner.getByRole("button", { name: "Deneme bildirimini kapat" }).click();
  await expect(banner).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  await expect(page.getByRole("region", { name: "Deneme süresi" })).toHaveCount(0);
});

test("Temel plan: second active warehouse and new locations are locked; existing ones stay visible (MOCK)", async ({ page }) => {
  await signedIn(page, { billing: createMockBilling({ plan: "basic" }) });
  await page.goto("/depo");
  await expect(page.getByRole("cell", { name: "Ana Depo (mock) Varsayılan" })).toBeVisible();
  await expect(page.getByRole("cell", { name: "Raflı Depo (mock)", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Yeni depo" })).toBeDisabled();
  const limitNote = page.getByRole("note").filter({ hasText: "Yeni aktif depo eklenemiyor" });
  await expect(limitNote).toContainText("Profesyonel pakette");
  await expect(limitNote).toContainText("Aktif depo sayısı sınırı paketinizde 1; şu an 2 kullanılıyor.");
  await expect(limitNote.getByRole("link", { name: "Paket ve modülleri gör" })).toHaveAttribute("href", "/ayarlar/paket");

  await page.getByRole("button", { name: "Lokasyonlar — Raflı Depo (mock)" }).click();
  await expect(page.getByRole("cell", { name: "A-01", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Yeni lokasyon" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /lokasyonunu düzenle/ })).toHaveCount(0);
  await expect(page.getByRole("note").filter({ hasText: "Yeni lokasyon eklenemiyor" })).toContainText("Profesyonel pakette");

  // Editing a warehouse: location tracking cannot be switched on.
  await page.getByRole("button", { name: "Ana Depo (mock) deposunu düzenle" }).click();
  const dialog = page.getByRole("dialog", { name: /Depoyu düzenle/ });
  await expect(dialog.getByRole("checkbox", { name: "Raf/göz (lokasyon) takibi" })).toBeDisabled();
  await expect(dialog.getByRole("note")).toContainText("Profesyonel pakette");
  await expectNoSeriousViolations(page);
});

test("global 403 mapping: entitlement.limit_exceeded from the API is explained with the suggested plan (MOCK)", async ({ page }) => {
  const api = await signedIn(page);
  api.inventory!.overrides.set(`PATCH /api/v1/warehouses/${WAREHOUSE_IDS.main}`, (route) =>
    route.fulfill({
      status: 403,
      contentType: "application/problem+json",
      headers: { "Access-Control-Allow-Origin": "*" },
      body: JSON.stringify(
        problemBody(403, "entitlement.limit_exceeded", { feature: "max_warehouses", limit: 1, current: 1, suggestedPlan: "professional", suggestedModule: null }),
      ),
    }),
  );
  await page.goto("/depo");
  await page.getByRole("button", { name: "Ana Depo (mock) deposunu düzenle" }).click();
  const dialog = page.getByRole("dialog", { name: /Depoyu düzenle/ });
  await dialog.getByLabel("Depo adı").fill("Ana Depo yeni ad");
  await dialog.getByRole("button", { name: "Kaydet" }).click();
  const alert = dialog.getByRole("alert");
  await expect(alert).toContainText("Aktif depo sayısı sınırına ulaşıldı (1/1).");
  await expect(alert).toContainText("Daha yüksek sınır Profesyonel pakette.");
});

test("WhatsApp AI without the add-on: parsing locked, pending reviews still listed (MOCK)", async ({ page }) => {
  await signedIn(page, { billing: createMockBilling({ plan: "professional" }) });
  await page.goto("/siparisler");
  await expect(page.getByRole("link", { name: /WhatsApp siparişi \(paketinizde kapalı\)/ })).toBeVisible();
  await page.goto("/siparisler/whatsapp");
  await expect(page.getByRole("note")).toContainText("AI WhatsApp asistanı modülünde");
  await expect(page.getByRole("button", { name: "Mesajı ayrıştır" })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "İnceleme bekleyen mesajlar" })).toBeVisible();
  await expectNoSeriousViolations(page);
});

test.describe("mobile", () => {
  test.use({ viewport: MOBILE });

  test("plan page and trial banner on a phone pass axe (MOCK)", async ({ page }) => {
    await signedIn(page, { billing: createMockBilling({ plan: "trial", trialDaysLeft: 2 }) });
    await page.goto("/ayarlar/paket");
    await expect(page.getByRole("heading", { level: 1, name: "Paket ve modüller" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Deneme süresi" })).toContainText("2 gün sonra bitiyor");
    await expect(page.getByRole("list", { name: "Ek modüller" })).toBeVisible();
    await expectNoSeriousViolations(page);
  });
});
