import { expect, test } from "@playwright/test";
import { DESKTOP } from "./viewports";

test.use({ viewport: DESKTOP });

test("dialog traps focus, closes with Escape and restores focus", async ({ page }) => {
  await page.goto("/tasarim-sistemi");
  const trigger = page.getByRole("button", { name: "Diyaloğu aç" });
  await trigger.click();
  const dialog = page.getByRole("alertdialog", { name: "Siparişi iptal et" });
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAccessibleDescription(/geri alınamaz/);
  await expect(dialog.getByRole("button", { name: "Kapat" })).toBeFocused();
  // Tab cycles inside the modal: Kapat → Vazgeç → Siparişi iptal et.
  await page.keyboard.press("Tab");
  await expect(dialog.getByRole("button", { name: "Vazgeç" })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(trigger).toBeFocused();
});

test("toast is announced and can be dismissed", async ({ page }) => {
  await page.goto("/tasarim-sistemi");
  await page.getByRole("button", { name: "Bildirim göster" }).click();
  const region = page.getByRole("region", { name: "Bildirimler" });
  await expect(region.locator("[aria-live=polite]")).toHaveCount(1);
  await expect(region.getByText("Kaydedildi", { exact: true })).toBeVisible();
  await region.getByRole("button", { name: "Bildirimi kapat" }).click();
  await expect(region.getByText("Kaydedildi", { exact: true })).toBeHidden();
});

test("table switches density and shows loading and empty states", async ({ page }) => {
  await page.goto("/tasarim-sistemi");
  const table = page.getByRole("table", { name: "Örnek tablo (kurgusal veri)" });
  await expect(table.getByRole("columnheader", { name: "Miktar" })).toBeVisible();
  await expect(table.getByRole("row")).toHaveCount(10); // header + 9 rows

  await page.getByLabel("Durum", { exact: true }).selectOption("loading");
  await expect(table).toHaveAttribute("aria-busy", "true");

  await page.getByLabel("Durum", { exact: true }).selectOption("empty");
  await expect(table.getByRole("heading", { name: "Kayıt yok" })).toBeVisible();
});
