import path from "node:path";
import { defineConfig, devices } from "@playwright/test";

/**
 * WCAG 2.2 AA AUDIT (#54) — every web route, desktop + mobile, MOCK backend (same helpers as e2e/).
 *
 * Not the CI gate: `e2e/a11y.spec.ts` and the per-feature specs fail CI on critical/serious axe violations. This run
 * records ALL violations (every impact) per route into test-results/a11y-audit/*.json so the findings list in
 * docs/testing/a11y-2026-11.md can be regenerated. Files are *.audit.ts, which the main config does not match.
 *
 *   npm run build   (with the e2e env from ci-web.yml)
 *   E2E_PORT=3310 npx playwright test -c e2e/a11y-audit/playwright.audit.config.ts
 *   node e2e/a11y-audit/summarize.mjs
 */
const PORT = Number(process.env.E2E_PORT ?? 3200);
const baseURL = `http://localhost:${PORT}`;
const webRoot = path.resolve(__dirname, "..", "..");

export default defineConfig({
  testDir: ".",
  testMatch: "*.audit.ts",
  outputDir: path.join(webRoot, "test-results", "a11y-audit-artifacts"),
  fullyParallel: true,
  workers: process.env.CI ? 2 : 4,
  reporter: "list",
  use: { baseURL, locale: "tr-TR", trace: "off" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `npm run start -- -p ${PORT}`,
    cwd: webRoot,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 60_000,
  },
});
