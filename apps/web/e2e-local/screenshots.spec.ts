import { test } from "@playwright/test";

/** Refreshes docs/screenshots for the auth/onboarding screens (real local stack, no mocks). */
const DESKTOP = { width: 1280, height: 800 };
const MOBILE = { width: 390, height: 844 };
const MAILPIT = process.env.MAILPIT_URL ?? "http://127.0.0.1:54324";

test("auth and onboarding screenshots", async ({ page }) => {
  for (const [name, viewport] of [
    ["desktop", DESKTOP],
    ["mobile", MOBILE],
  ] as const) {
    await page.setViewportSize(viewport);
    await page.goto("/giris");
    await page.getByRole("button", { name: "Giriş yap" }).click(); // show validation state
    await page.screenshot({ path: `docs/screenshots/${name}-giris.png`, fullPage: true });
    await page.goto("/kayit");
    await page.screenshot({ path: `docs/screenshots/${name}-kayit.png`, fullPage: true });
  }

  const email = `shot-${Date.now()}@example.test`;
  await page.setViewportSize(DESKTOP);
  await page.goto("/kayit");
  await page.getByLabel("Ad soyad").fill("Ekran Görüntüsü");
  await page.getByLabel("E-posta").fill(email);
  await page.locator("input[name=password]").fill("Ekran-Goruntu-1");
  await page.locator("input[name=confirm]").fill("Ekran-Goruntu-1");
  await page.getByRole("button", { name: "Hesap oluştur" }).click();
  await page.getByText("doğrulama bağlantısı gönderdik").waitFor();
  await new Promise((r) => setTimeout(r, 1000));
  const { messages } = (await (await fetch(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json()) as {
    messages: { ID: string }[];
  };
  const message = (await (await fetch(`${MAILPIT}/api/v1/message/${messages[0].ID}`)).json()) as { Text: string };
  await page.goto(message.Text.match(/https?:\/\/\S+\/verify\?\S+/)![0].replace(/&amp;/g, "&"));
  await page.getByLabel("İşletme adı").fill("Ekran Motor Yedek Parça");
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByLabel(/Motosiklet yedek parça/).check();
  await page.screenshot({ path: "docs/screenshots/desktop-onboarding-sektor.png", fullPage: true });
  await page.getByRole("button", { name: "Devam" }).click();
  await page.setViewportSize(MOBILE);
  await page.screenshot({ path: "docs/screenshots/mobile-onboarding-ticari.png", fullPage: true });
});
