import { expect, test } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";

test.describe("shell — desktop", () => {
  test.use({ viewport: DESKTOP });

  test("renders sidebar, header and an honest placeholder", async ({ page }) => {
    await page.goto("/pano");
    await expect(page.getByRole("heading", { level: 1, name: "Pano" })).toBeVisible();
    const nav = page.getByRole("navigation", { name: "Ana menü" });
    await expect(nav).toBeVisible();
    for (const label of ["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Müşteriler", "Raporlar", "İçe aktarma", "Ayarlar"]) {
      await expect(nav.getByRole("link", { name: label, exact: true })).toBeVisible();
    }
    await expect(page.getByText("Giriş yapılmadı")).toBeVisible();
    await expect(page.getByRole("heading", { name: "Bu modül henüz geliştiriliyor" })).toBeVisible();
    // The real API status widget is still wired up (API is not running in this test → error text).
    await expect(page.getByRole("status")).toContainText("API");
    await expect(page.getByRole("button", { name: "Menüyü aç" })).toBeHidden();
  });

  test("navigates between modules and marks the active link", async ({ page }) => {
    await page.goto("/pano");
    const nav = page.getByRole("navigation", { name: "Ana menü" });
    await nav.getByRole("link", { name: "Ürünler" }).click();
    await expect(page).toHaveURL(/\/urunler$/);
    await expect(page.getByRole("heading", { level: 1, name: "Ürünler" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Ürünler" })).toHaveAttribute("aria-current", "page");
    await expect(nav.getByRole("link", { name: "Pano" })).not.toHaveAttribute("aria-current", "page");
    await expect(page).toHaveTitle("Ürünler · BayiOS");
  });

  test("collapses the sidebar to an icon rail and remembers it", async ({ page }) => {
    await page.goto("/stok");
    await page.getByRole("button", { name: "Menüyü daralt" }).click();
    const expand = page.getByRole("button", { name: "Menüyü genişlet" });
    await expect(expand).toHaveAttribute("aria-expanded", "false");
    // Labels remain the accessible names of the icon links.
    await expect(page.getByRole("link", { name: "Siparişler" })).toBeVisible();
    await page.reload();
    await expect(page.getByRole("button", { name: "Menüyü genişlet" })).toBeVisible();
    await page.getByRole("button", { name: "Menüyü genişlet" }).click();
    await expect(page.getByRole("button", { name: "Menüyü daralt" })).toBeVisible();
  });

  test("skip link moves focus to the main content", async ({ page }) => {
    await page.goto("/pano");
    await page.keyboard.press("Tab");
    const skip = page.getByRole("link", { name: "İçeriğe geç" });
    await expect(skip).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.locator("main#main")).toBeFocused();
  });

  test("landing page still links into the app", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { level: 1, name: "BayiOS" })).toBeVisible();
    await expect(page.getByRole("status")).toBeVisible();
    await page.getByRole("link", { name: "Panele git" }).click();
    await expect(page).toHaveURL(/\/pano$/);
  });
});

test.describe("shell — mobile", () => {
  test.use({ viewport: MOBILE, hasTouch: true, isMobile: true });

  test("hides the sidebar and opens an off-canvas drawer", async ({ page }) => {
    await page.goto("/pano");
    await expect(page.getByRole("navigation", { name: "Ana menü" })).toBeHidden();
    const open = page.getByRole("button", { name: "Menüyü aç" });
    await expect(open).toHaveAttribute("aria-expanded", "false");

    await open.click();
    const drawer = page.getByRole("dialog", { name: "Menü" });
    await expect(drawer).toBeVisible();
    await expect(open).toHaveAttribute("aria-expanded", "true");

    await drawer.getByRole("link", { name: "Siparişler" }).click();
    await expect(page).toHaveURL(/\/siparisler$/);
    await expect(drawer).toBeHidden();
    await expect(page.getByRole("heading", { level: 1, name: "Siparişler" })).toBeVisible();
  });

  test("drawer closes with Escape and the close button, returning focus", async ({ page }) => {
    await page.goto("/stok");
    const open = page.getByRole("button", { name: "Menüyü aç" });
    await open.click();
    const drawer = page.getByRole("dialog", { name: "Menü" });
    await expect(drawer).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toBeHidden();
    await expect(open).toBeFocused();

    await open.click();
    await drawer.getByRole("button", { name: "Menüyü kapat" }).click();
    await expect(drawer).toBeHidden();
  });
});
