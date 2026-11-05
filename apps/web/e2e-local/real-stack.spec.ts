import { expect, test, type Page } from "@playwright/test";

/**
 * REAL END-TO-END — no mocks. Needs the real stack running (`bash infra/ci/e2e-stack.sh up`, apps/web/README.md
 * "Gerçek yığın E2E"): local Supabase CLI (ES256 signing key) + BayiOS API validating its JWKS + this web app on :3200.
 * CI: .github/workflows/e2e-real.yml (`npm run test:e2e:real`).
 */
const MAILPIT = process.env.MAILPIT_URL ?? "http://127.0.0.1:54324";

async function confirmationLink(email: string): Promise<string> {
  for (let attempt = 0; attempt < 20; attempt++) {
    const search = await fetch(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`);
    const { messages } = (await search.json()) as { messages: { ID: string }[] };
    if (messages.length > 0) {
      const message = (await (await fetch(`${MAILPIT}/api/v1/message/${messages[0].ID}`)).json()) as { Text: string };
      const link = message.Text.match(/https?:\/\/\S+\/verify\?\S+/)?.[0];
      if (link) return link.replace(/&amp;/g, "&");
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`No confirmation e-mail for ${email}`);
}

async function signUp(page: Page, email: string, password: string) {
  await page.goto("/kayit");
  await page.getByLabel("Ad soyad").fill("Yerel Test Kullanıcısı");
  await page.getByLabel("E-posta").fill(email);
  await page.locator("input[name=password]").fill(password);
  await page.locator("input[name=confirm]").fill(password);
  await page.getByRole("button", { name: "Hesap oluştur" }).click();
  await expect(page.getByText("doğrulama bağlantısı gönderdik")).toBeVisible();
}

test("sign-up → e-mail confirmation → onboarding → dashboard → sign-out → sign-in", async ({ page }) => {
  const stamp = Date.now();
  const email = `e2e-${stamp}@example.test`;
  const password = "Yerel-Test-1234";
  const slug = `yerel-${stamp}`;

  await signUp(page, email, password);

  // The link goes to Supabase /verify, which redirects to /auth/callback?code=… (PKCE) → onboarding.
  await page.goto(await confirmationLink(email));
  await expect(page).toHaveURL(/\/isletme-olustur$/);
  await expect(page.getByRole("heading", { name: "İşletmenizi oluşturun" })).toBeVisible();

  await page.getByLabel("İşletme adı").fill(`Yerel Motor ${stamp}`);
  await page.getByLabel("İşletme adresi (kısa ad)").fill(slug);
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByLabel(/Motosiklet yedek parça/).check();
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByLabel("Para birimi").selectOption("EUR");
  await page.getByLabel("Varsayılan KDV oranı").selectOption("10");
  await page.getByLabel("Fiyat gösterimi").selectOption("included");
  await page.getByLabel("Negatif stok").selectOption("warn");
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByText(slug)).toBeVisible();

  const created = page.waitForResponse((r) => r.url().endsWith("/api/v1/tenants") && r.request().method() === "POST");
  const applied = page.waitForResponse((r) => r.url().endsWith("/sector-templates/motorcycle_parts/apply"));
  const patched = page.waitForResponse((r) => r.url().endsWith("/api/v1/tenant/settings") && r.request().method() === "PATCH");
  const currency = page.waitForResponse((r) => r.url().endsWith("/api/v1/tenant") && r.request().method() === "PATCH");
  await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();
  const createResponse = await created;
  expect(createResponse.status()).toBe(201);
  expect(createResponse.request().headers()["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
  const applyResponse = await applied;
  expect(applyResponse.status()).toBe(200);
  const applyBody = (await applyResponse.json()) as { template: string; unitsCreated: unknown[]; fieldsCreated: unknown[] };
  expect(applyBody.template).toBe("motorcycle_parts");
  expect(applyBody.unitsCreated.length + applyBody.fieldsCreated.length).toBeGreaterThan(0);
  expect((await patched).status()).toBe(200);
  expect((await currency).status()).toBe(200);

  await expect(page).toHaveURL(/\/pano$/);
  await expect(page.getByRole("heading", { level: 1, name: "Pano" })).toBeVisible();
  await expect(page.getByTestId("session-user")).toHaveText("Yerel Test Kullanıcısı");
  await expect(page.getByRole("banner").getByText("İşletme sahibi")).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Ana menü" }).getByRole("link", { name: "Ayarlar" })).toBeVisible();
  await page.screenshot({ path: "test-results/real-stack-pano.png", fullPage: true });

  await page.getByRole("button", { name: "Çıkış yap" }).click();
  await expect(page).toHaveURL(/\/giris$/);
  await page.goto("/pano");
  await expect(page).toHaveURL(/\/giris\?next=%2Fpano$|\/giris$/);

  await page.getByLabel("E-posta").fill(email);
  await page.locator("input[name=password]").fill(password);
  await page.getByRole("button", { name: "Giriş yap" }).click();
  await expect(page).toHaveURL(/\/pano$/);
  await expect(page.getByTestId("session-user")).toHaveText("Yerel Test Kullanıcısı");
});

test("wrong password shows a Turkish error and stays on sign-in", async ({ page }) => {
  await page.goto("/giris");
  await page.getByLabel("E-posta").fill(`nobody-${Date.now()}@example.test`);
  await page.locator("input[name=password]").fill("Yanlis-Sifre-1");
  await page.getByRole("button", { name: "Giriş yap" }).click();
  await expect(page.getByRole("form", { name: "Giriş formu" }).getByRole("alert")).toHaveText("E-posta adresi veya şifre hatalı.");
  await expect(page).toHaveURL(/\/giris$/);
});
