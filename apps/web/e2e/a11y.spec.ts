import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";

const WCAG_TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

/** Fails on critical and serious axe violations; prints a readable summary when it does. */
async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const routes = ["/", "/pano", "/urunler", "/ice-aktarma", "/tasarim-sistemi"];

for (const [name, viewport] of [
  ["desktop", DESKTOP],
  ["mobile", MOBILE],
] as const) {
  test.describe(`axe — ${name}`, () => {
    test.use({ viewport });

    for (const route of routes) {
      test(`${route} has no critical/serious violations`, async ({ page }) => {
        await page.goto(route);
        await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
        await expectNoSeriousViolations(page);
      });
    }

    test("/tasarim-sistemi in dark mode has no critical/serious violations", async ({ page }) => {
      await page.emulateMedia({ colorScheme: "dark" });
      await page.goto("/tasarim-sistemi");
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expectNoSeriousViolations(page);
    });
  });
}

test.describe("axe — open overlays", () => {
  test("mobile drawer", async ({ page }) => {
    await page.setViewportSize(MOBILE);
    await page.goto("/pano");
    await page.getByRole("button", { name: "Menüyü aç" }).click();
    await expect(page.getByRole("dialog", { name: "Menü" })).toBeVisible();
    await expectNoSeriousViolations(page);
  });

  test("dialog", async ({ page }) => {
    await page.setViewportSize(DESKTOP);
    await page.goto("/tasarim-sistemi");
    await page.getByRole("button", { name: "Diyaloğu aç" }).click();
    await expect(page.getByRole("alertdialog")).toBeVisible();
    await expectNoSeriousViolations(page);
  });
});
