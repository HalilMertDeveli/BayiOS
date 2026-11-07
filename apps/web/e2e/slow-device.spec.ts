/**
 * Slow device (#152): the CPU is throttled 6× (Chromium DevTools protocol), so React commits an update made after an
 * `await` later than the next animation frame, and a URL filter change takes longer to commit.
 * - Focus: an error / notice that appears after a failed save must take focus (not stay on <body> or a disabled button).
 * - URL filters: two quick changes must both end up in the URL (the second builds on the pending one, not the stale URL).
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts). All data is synthetic.
 */
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { API_URL, problem, signedIn } from "./support/mock-backend";
import { createMockCatalog } from "./support/mock-catalog";

test.use({ viewport: DESKTOP });

async function throttleCpu(page: Page, rate = 6) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Emulation.setCPUThrottlingRate", { rate });
}

/** The focused element's text (empty for <body>). */
const focusedText = (page: Page) => page.evaluate(() => (document.activeElement === document.body ? "" : (document.activeElement?.textContent ?? "")));

test("yavaş cihaz: kaydedilemeyen ayarlarda hata kutusu odağı alır", async ({ page }) => {
  await signedIn(page);
  // MOCK: the save fails on the server (registered last, so it runs before the default settings handler).
  await page.route(`${API_URL}/api/v1/tenant/settings`, (route) =>
    route.request().method() === "PATCH"
      ? route.fulfill({ status: 500, contentType: "application/problem+json", body: JSON.stringify(problem(500, "server.error")) })
      : route.fallback(),
  );
  await page.goto("/ayarlar");
  const form = page.getByRole("form", { name: "Sipariş ayarları" });
  await expect(form.getByRole("textbox", { name: /Sipariş numarası öneki/ })).toHaveValue("SIP");
  await throttleCpu(page);
  await form.getByRole("combobox", { name: "Stok yetmediğinde" }).selectOption("warn");
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  const alert = form.getByRole("alert").filter({ hasText: "Ayarlar kaydedilemedi" });
  await expect(alert).toBeVisible();
  await expect(alert).toBeFocused();
});

test("yavaş cihaz: 412 uyarısı odağı alır (kaydet düğmesi artık pasif)", async ({ page }) => {
  const api = await signedIn(page);
  await page.goto("/ayarlar");
  const form = page.getByRole("form", { name: "Sipariş ayarları" });
  await form.getByRole("textbox", { name: /Sipariş numarası öneki/ }).fill("BAYI");
  // Someone else saved meanwhile (MOCK: the server copy moves on).
  api.settings = { ...api.settings, orderNumberPrefix: "ORD" };
  api.settingsVersion += 1;
  await throttleCpu(page);
  await form.getByRole("button", { name: "Ayarları kaydet" }).click();
  const notice = page.getByRole("alert").filter({ hasText: "Ayarlar başkası tarafından değiştirildi" });
  await expect(notice).toBeVisible();
  await expect(notice).toBeFocused();
  expect(await focusedText(page)).toContain("Güncel ayarları yükle");
});

test("yavaş cihaz: ürün listesinde art arda iki filtre ikisini de URL'de tutar", async ({ page }) => {
  await signedIn(page, { catalog: createMockCatalog({ products: 20 }) });
  await page.goto("/urunler");
  const status = page.getByRole("combobox", { name: "Durum" });
  await expect(status).toBeEnabled();
  await throttleCpu(page);
  // No wait between the two: the second change happens while the first navigation is still pending.
  await status.selectOption("archived");
  await page.getByRole("combobox", { name: "Sıralama" }).selectOption("name");
  await expect(page).toHaveURL(/status=archived/);
  await expect(page).toHaveURL(/sort=name/);
  // And the URL stays that way once both navigations have settled.
  await page.waitForTimeout(500);
  expect(new URL(page.url()).search).toBe("?status=archived&sort=name");
});
