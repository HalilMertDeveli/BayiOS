/**
 * Ayarlar → Sipariş ayarları (#92, #111 web).
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts): GET/PATCH
 * /tenant/settings keep an in-memory copy with an ETag version (If-Match → 412) and validate the prefix like the API.
 * All data is synthetic; nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";

test.use({ viewport: DESKTOP });

const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const patches = (api: Api) => api.requests.filter((r) => r.method === "PATCH" && r.path === "/api/v1/tenant/settings");

test("sahip: onay zorunluluğu ve sipariş numarası öneki kaydedilir (merge patch + If-Match)", async ({ page }) => {
  const api = await signedIn(page);
  await page.goto("/ayarlar");
  await expect(page.getByRole("heading", { level: 1, name: "Ayarlar" })).toBeVisible();
  const form = page.getByRole("form", { name: "Sipariş ayarları" });
  const prefix = form.getByRole("textbox", { name: /Sipariş numarası öneki/ });
  await expect(prefix).toHaveValue("SIP");
  await expect(form.getByRole("checkbox", { name: "Siparişler onaydan geçsin" })).toBeChecked();
  await expect(form.getByRole("button", { name: "Ayarları kaydet" })).toBeDisabled();
  await expectNoSeriousViolations(page);

  // Client-side check, same rule as the API: nothing is sent.
  await prefix.fill("1-WEB");
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  await expect(form.getByText("Önek bir harfle (A–Z) başlamalı.")).toBeVisible();
  expect(patches(api)).toHaveLength(0);

  await prefix.fill("web");
  await expect(prefix).toHaveValue("WEB");
  await expect(form.getByText(/WEB-\d{4}-000123/)).toBeVisible();
  await form.getByRole("checkbox", { name: "Siparişler onaydan geçsin" }).uncheck();
  await expect(form.getByText(/hemen onaylanır ve stok ayrılır/)).toBeVisible();
  // OD-09: stock never goes below zero — a fixed rule, not a choice.
  await expect(form.getByText("Stok eksiye düşmez")).toBeVisible();
  await expect(form.getByRole("combobox", { name: "Stok yetmediğinde" })).toHaveCount(0);
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  await expect(page.getByText("Sipariş ayarları kaydedildi")).toBeVisible();

  const [patch] = patches(api);
  expect(patch.headers["if-match"]).toBe('W/"0"');
  expect(patch.headers["content-type"]).toContain("application/merge-patch+json");
  expect(patch.body).toEqual({ requireOrderApproval: false, orderNumberPrefix: "WEB" });
  expect(api.settings).toMatchObject({ requireOrderApproval: false, orderNumberPrefix: "WEB", negativeStockPolicy: "block" });
  await expect(form.getByRole("button", { name: "Ayarları kaydet" })).toBeDisabled();

  // A second save uses the new ETag.
  await form.getByRole("combobox", { name: "Varsayılan KDV oranı" }).selectOption("10");
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  await expect.poll(() => patches(api).length).toBe(2);
  expect(patches(api)[1].headers["if-match"]).toBe('W/"1"');
  expect(patches(api)[1].body).toEqual({ defaultTaxRate: 10 });
});

test("412: başka bir yönetici önce kaydettiyse uyarı; güncel ayarlar yüklenir", async ({ page }) => {
  const api = await signedIn(page);
  await page.goto("/ayarlar");
  const form = page.getByRole("form", { name: "Sipariş ayarları" });
  await form.getByRole("textbox", { name: /Sipariş numarası öneki/ }).fill("BAYI");
  // Someone else saved meanwhile (MOCK: the server copy moves on).
  api.settings = { ...api.settings, orderNumberPrefix: "ORD" };
  api.settingsVersion += 1;
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Ayarlar başkası tarafından değiştirildi" })).toBeVisible();
  expect(api.settings.orderNumberPrefix).toBe("ORD");
  await page.getByRole("button", { name: "Güncel ayarları yükle" }).click();
  await expect(page.getByRole("form", { name: "Sipariş ayarları" }).getByRole("textbox", { name: /Sipariş numarası öneki/ })).toHaveValue("ORD");
  await expect(page.getByText("Ayarlar başkası tarafından değiştirildi")).toBeHidden();
});

test("422 from the API is shown on the field in Turkish", async ({ page }) => {
  await signedIn(page);
  await page.goto("/ayarlar");
  const form = page.getByRole("form", { name: "Sipariş ayarları" });
  // A value the client accepts but the (MOCK) API refuses would be rare; force it with a route override.
  await page.route("**/api/v1/tenant/settings", async (route) => {
    if (route.request().method() !== "PATCH") return route.fallback();
    return route.fulfill({
      status: 422,
      contentType: "application/problem+json",
      headers: { "Access-Control-Allow-Origin": "*" },
      body: JSON.stringify({ status: 422, code: "validation.failed", errors: { orderNumberPrefix: ["Must be 1–10 characters of A–Z and 0–9, starting with a letter."] } }),
    });
  });
  await form.getByRole("textbox", { name: /Sipariş numarası öneki/ }).fill("ABC");
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  await expect(form.getByText("Önek 1–10 karakter olmalı; A–Z ve 0–9 kullanın, harfle başlayın.")).toBeVisible();
});

test("yönetici (tenant.settings.manage yok): ayarlar kilitli, istek yok", async ({ page }) => {
  const api = await signedIn(page, { memberships: [MANAGER] });
  await page.goto("/ayarlar");
  await expect(page.getByText("Ayarları değiştirme yetkiniz yok")).toBeVisible();
  expect(api.requests.some((r) => r.path === "/api/v1/tenant/settings")).toBe(false);
  await expect(page.getByRole("link", { name: /Özel alanlar/ })).toBeVisible();
});

test("mobil: form telefona sığar ve erişilebilir", async ({ page }) => {
  await page.setViewportSize(MOBILE);
  await signedIn(page);
  await page.goto("/ayarlar");
  await expect(page.getByRole("form", { name: "Sipariş ayarları" })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await expectNoSeriousViolations(page);
});
