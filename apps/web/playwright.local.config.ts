import { defineConfig, devices } from "@playwright/test";

/**
 * Real end-to-end run against the local stack (local Supabase + API + `npm run start -- -p 3200`).
 * Nothing is mocked. Not used by CI. See apps/web/README.md.
 */
export default defineConfig({
  testDir: "./e2e-local",
  fullyParallel: false,
  workers: 1,
  reporter: "list",
  use: {
    baseURL: "http://localhost:3200",
    locale: "tr-TR",
    trace: "retain-on-failure",
    viewport: { width: 1280, height: 800 },
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } } }],
});
