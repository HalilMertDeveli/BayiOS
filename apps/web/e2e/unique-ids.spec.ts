/**
 * A11y audit 2026-11 A-1 (WCAG 1.3.1 / 4.1.2): Cache Components keeps the previous route mounted but hidden
 * (React Activity), so a hard-coded id appeared twice and `label for` / `aria-describedby` / `aria-labelledby`
 * resolved to the hidden copy. Every id is now per-instance (React useId). These flows keep two screens with the
 * same fields in the DOM and assert that ids are unique and the visible field is labelled by its own label.
 * MOCKED Supabase Auth + API (support/mock-backend.ts).
 */
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { signedIn } from "./support/mock-backend";
import { createMockAssistant } from "./support/mock-assistant";
import { createMockCatalog } from "./support/mock-catalog";
import { createMockCommerce } from "./support/mock-commerce";
import { createMockOrders, orderId } from "./support/mock-orders";

test.use({ viewport: DESKTOP });

function setup() {
  const catalog = createMockCatalog({ products: 3 });
  return { catalog, commerce: createMockCommerce(catalog), orders: createMockOrders(), assistant: createMockAssistant() };
}

/** Ids that occur more than once, and id references (for / aria-*) that point at nothing. */
async function idProblems(page: Page) {
  return page.evaluate(() => {
    const counts = new Map<string, number>();
    for (const el of document.querySelectorAll("[id]")) counts.set(el.id, (counts.get(el.id) ?? 0) + 1);
    const duplicates = [...counts].filter(([, n]) => n > 1).map(([id]) => id);
    const dangling: string[] = [];
    for (const el of document.querySelectorAll("[for],[aria-labelledby],[aria-describedby],[aria-controls]")) {
      for (const attr of ["for", "aria-labelledby", "aria-describedby", "aria-controls"]) {
        for (const ref of (el.getAttribute(attr) ?? "").split(/\s+/).filter(Boolean)) {
          if (!document.getElementById(ref)) dangling.push(`${attr}=${ref}`);
        }
      }
    }
    return { duplicates, dangling };
  });
}

/** The visible searchbox's own label (the `for` target of a label) is the visible one. */
async function expectVisibleFieldOwnsItsLabel(page: Page, name: string | RegExp) {
  const box = page.getByRole("searchbox", { name });
  await expect(box).toBeVisible();
  const ok = await box.evaluate((input) => {
    const label = document.querySelector(`label[for="${CSS.escape(input.id)}"]`) as HTMLElement | null;
    return !!label && label.offsetParent !== null && (input as HTMLInputElement).labels?.[0] === label;
  });
  expect(ok).toBe(true);
}

test("WhatsApp entry → review: the customer field is not duplicated with the hidden entry screen", async ({ page }) => {
  await signedIn(page, setup());
  await page.goto("/siparisler/whatsapp");
  await expect(page.getByRole("heading", { level: 1, name: "WhatsApp siparişi" })).toBeVisible();
  await page.getByRole("textbox", { name: /^WhatsApp mesajı/ }).fill("Merhaba\n3 adet MOCK-001-1");
  await page.getByRole("button", { name: "Mesajı ayrıştır" }).click();
  await expect(page).toHaveURL(/\/siparisler\/whatsapp\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { level: 1, name: "WhatsApp siparişini incele" })).toBeVisible();
  await page.waitForLoadState("networkidle");

  // The entry screen is still in the DOM (hidden): two customer pickers exist at once.
  expect(await page.locator("input[type=search]").count()).toBeGreaterThanOrEqual(2);
  expect(await idProblems(page)).toEqual({ duplicates: [], dangling: [] });
  await expectVisibleFieldOwnsItsLabel(page, /^Müşteri/);
});

test("new order ↔ draft editor and the main sections: ids stay unique while earlier routes stay mounted", async ({ page }) => {
  test.setTimeout(90_000);
  await signedIn(page, setup());
  await page.goto("/siparisler");
  await page.getByRole("link", { name: "Yeni sipariş" }).click();
  await expect(page).toHaveURL(/\/siparisler\/yeni$/);
  await expect(page.getByRole("searchbox", { name: "Müşteri" })).toBeVisible();
  await page.goto("/siparisler/yeni");
  await page.getByRole("searchbox", { name: "Müşteri" }).fill("Mock");
  await page.getByRole("button", { name: "Müşteri ara" }).click();
  await page.getByRole("button", { name: /Mock Müşteri 001/ }).click();
  const scanner = page.getByRole("textbox", { name: "Ürün ekle" });
  await scanner.fill("MOCK-001-1");
  await scanner.press("Enter");
  await expect(page.getByText("MOCK-001-1 eklendi.")).toBeVisible();
  await page.getByRole("button", { name: "Taslak oluştur" }).click();
  // Client navigation to the draft: /siparisler/yeni (same editor component) stays mounted, hidden.
  await expect(page).toHaveURL(new RegExp(`/siparisler/${orderId(1000)}$`));
  await page.waitForLoadState("networkidle");
  expect(await idProblems(page)).toEqual({ duplicates: [], dangling: [] });

  const nav = page.getByRole("navigation", { name: "Ana menü" });
  for (const label of ["Ürünler", "Stok", "Depo", "Müşteriler", "Fiyat listeleri", "Raporlar", "İçe aktarma", "Ayarlar", "Pano"]) {
    await nav.getByRole("link", { name: label, exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: label })).toBeVisible();
    await page.waitForLoadState("networkidle");
    expect(await idProblems(page), label).toEqual({ duplicates: [], dangling: [] });
  }
});
