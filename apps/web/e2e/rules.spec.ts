/**
 * Ayarlar → İş kuralları (#129 item 6, roles-permissions §4).
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts): GET/PATCH
 * /tenant/settings keep an in-memory copy with an ETag version (If-Match → 412). All data is synthetic.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { API_URL, TENANT_A, problem, signedIn } from "./support/mock-backend";

test.use({ viewport: DESKTOP });

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const patches = (api: Api) => api.requests.filter((r) => r.method === "PATCH" && r.path === "/api/v1/tenant/settings");
const meCalls = (api: Api) => api.requests.filter((r) => r.path === "/api/v1/me").length;

test("sahip: kurallar kaydedilir (yalnızca değişenler, If-Match) ve /me yenilenir", async ({ page }) => {
  const api = await signedIn(page);
  await page.goto("/ayarlar/is-kurallari");
  await expect(page.getByRole("heading", { level: 1, name: "İş kuralları" })).toBeVisible();
  const form = page.getByRole("form", { name: "İş kuralları" });
  await expect(form.getByRole("button", { name: "Kuralları kaydet" })).toBeDisabled();
  await expect(form.getByText("Yakında · henüz uygulanmıyor")).toBeVisible();
  await expectNoSeriousViolations(page);

  await form.getByRole("checkbox", { name: "Satış personeli sipariş onaylayabilir" }).check();
  await form.getByRole("textbox", { name: /iskonto sınırı/ }).fill("7,5");
  await expect(form.getByText("Satış personeli satır başına en fazla %7,5 iskonto uygulayabilir; birim fiyatı elle değiştiremez.")).toBeVisible();
  await form.getByRole("checkbox", { name: "Depo personeli barkod atayabilir" }).check();
  await form.getByRole("checkbox", { name: "İzleyici alış fiyatını görebilir" }).check();
  const before = meCalls(api);
  await form.getByRole("button", { name: "Kuralları kaydet" }).click();
  await expect(page.getByText("İş kuralları kaydedildi")).toBeVisible();

  const [patch] = patches(api);
  expect(patch.headers["if-match"]).toBe('W/"0"');
  expect(patch.headers["content-type"]).toContain("application/merge-patch+json");
  expect(patch.body).toEqual({ salesCanApproveOrders: true, salesMaxDiscountPercent: 7.5, staffCanAssignBarcode: true, showCostToViewer: true });
  await expect.poll(() => meCalls(api)).toBeGreaterThan(before);
  await expect(form.getByRole("button", { name: "Kuralları kaydet" })).toBeDisabled();
});

test("412: başka bir yönetici önce kaydettiyse uyarı; güncel kurallar yüklenir", async ({ page }) => {
  const api = await signedIn(page);
  await page.goto("/ayarlar/is-kurallari");
  const form = page.getByRole("form", { name: "İş kuralları" });
  await form.getByRole("checkbox", { name: "Depo personeli mal kabulü onaylayabilir" }).check();
  api.settings = { ...api.settings, staffCanApproveReceipt: true, showCostToViewer: true };
  api.settingsVersion += 1;
  await form.getByRole("button", { name: "Kuralları kaydet" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Kurallar başkası tarafından değiştirildi" })).toBeVisible();
  await page.getByRole("button", { name: "Güncel kuralları yükle" }).click();
  await expect(page.getByRole("checkbox", { name: "İzleyici alış fiyatını görebilir" })).toBeChecked();
  await expect(page.getByText("Kurallar başkası tarafından değiştirildi")).toBeHidden();
});

test("422: API alan hatası Türkçe ve alanın altında", async ({ page }) => {
  await signedIn(page);
  await page.goto("/ayarlar/is-kurallari");
  await page.route(`${API_URL}/api/v1/tenant/settings`, async (route) => {
    if (route.request().method() !== "PATCH") return route.fallback();
    return route.fulfill({
      status: 422,
      contentType: "application/problem+json",
      headers: { "Access-Control-Allow-Origin": "*" },
      body: JSON.stringify(problem(422, "validation.failed", { errors: { salesMaxDiscountPercent: ["Must be between 0 and 100."] } })),
    });
  });
  const form = page.getByRole("form", { name: "İş kuralları" });
  await form.getByRole("textbox", { name: /iskonto sınırı/ }).fill("10");
  await form.getByRole("button", { name: "Kuralları kaydet" }).click();
  await expect(form.getByRole("textbox", { name: /iskonto sınırı/ })).toHaveAttribute("aria-invalid", "true");
  await expect(form.getByText("0 ile 100 arasında, en fazla 2 ondalıklı bir yüzde girin (ör. 5 veya 7,5).").first()).toBeVisible();
});

for (const role of ["manager", "sales", "viewer", "warehouse_staff"]) {
  test(`${role}: iş kuralları kilitli, istek yok`, async ({ page }) => {
    const api = await signedIn(page, { memberships: [{ ...TENANT_A, role }] });
    await page.goto("/ayarlar/is-kurallari");
    await expect(page.getByText("İş kurallarını değiştirme yetkiniz yok")).toBeVisible();
    expect(api.requests.some((r) => r.path === "/api/v1/tenant/settings")).toBe(false);
  });
}

test("mobil: kurallar telefona sığar ve erişilebilir", async ({ page }) => {
  await page.setViewportSize(MOBILE);
  await signedIn(page);
  await page.goto("/ayarlar/is-kurallari");
  await expect(page.getByRole("form", { name: "İş kuralları" })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await expectNoSeriousViolations(page);
});
