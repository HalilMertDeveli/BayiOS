import path from "node:path";
import { defineConfig, devices } from "@playwright/test";

/**
 * Product screenshots of the main screens (UI polish review) — MOCK backend only (same helpers as e2e/), desktop
 * 1280 + mobile 390, light + dark. Not a CI gate; files are *.shots.ts, which the main config does not match.
 *
 *   npm run build   (with the e2e env from ci-web.yml)
 *   SHOTS_DIR=docs/screenshots/ui-polish/after E2E_PORT=3310 npx playwright test -c e2e/screenshots/playwright.shots.config.ts
 */
const PORT = Number(process.env.E2E_PORT ?? 3200);
const baseURL = `http://localhost:${PORT}`;
const webRoot = path.resolve(__dirname, "..", "..");

export default defineConfig({
  testDir: ".",
  testMatch: "*.shots.ts",
  outputDir: path.join(webRoot, "test-results", "shots-artifacts"),
  fullyParallel: true,
  workers: 4,
  reporter: "list",
  use: { baseURL, locale: "tr-TR", timezoneId: "Europe/Istanbul", trace: "off" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `npm run start -- -p ${PORT}`,
    cwd: webRoot,
    url: baseURL,
    reuseExistingServer: true,
    timeout: 60_000,
  },
});
