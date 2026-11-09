import { defineConfig, devices } from "@playwright/test";

/**
 * Real end-to-end run against the real stack (Supabase Auth + API + Postgres + `next start -p 3200`). Nothing is mocked.
 * CI: .github/workflows/e2e-real.yml (`npm run test:e2e:real`). Locally: apps/web/README.md "Gerçek yığın E2E".
 *
 * No retries on purpose: a flaky real-stack test is a bug to root-cause, not to hide.
 */
export default defineConfig({
  testDir: "./e2e-local",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [["github"], ["list"], ["html", { open: "never", outputFolder: "playwright-report/real" }]] : "list",
  outputDir: "test-results/real",
  use: {
    baseURL: process.env.E2E_WEB_URL ?? "http://localhost:3200",
    locale: "tr-TR",
    timezoneId: "Europe/Istanbul",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    viewport: { width: 1280, height: 800 },
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } } }],
});
