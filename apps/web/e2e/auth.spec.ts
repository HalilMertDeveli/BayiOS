/**
 * Auth screens and route protection. Supabase Auth and the BayiOS API are MOCKED at the network layer
 * (page.route) — see support/mock-backend.ts. The proxy redirect tests use no mock at all.
 */
import { expect, test } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { USER, addSessionCookie, mockApi, mockSupabaseAuth, signedIn } from "./support/mock-backend";

test.use({ viewport: DESKTOP });

test.describe("route protection (no session)", () => {
  for (const path of ["/pano", "/urunler", "/ayarlar", "/tasarim-sistemi", "/isletme-olustur", "/isletme-sec"]) {
    test(`${path} redirects to sign-in with next`, async ({ page }) => {
      await page.goto(path);
      await expect(page).toHaveURL(`/giris?next=${encodeURIComponent(path)}`);
      await expect(page.getByRole("heading", { level: 1, name: "Giriş yap" })).toBeVisible();
    });
  }

  test("public pages stay reachable", async ({ page }) => {
    for (const path of ["/", "/giris", "/kayit", "/sifre-sifirla"]) {
      const response = await page.goto(path);
      expect(response?.status(), path).toBe(200);
      await expect(page).toHaveURL(path);
    }
  });

  test("an external next parameter is ignored (no open redirect)", async ({ page }) => {
    await mockSupabaseAuth(page);
    await mockApi(page);
    await page.goto("/giris?next=//evil.example/x");
    await page.getByLabel("E-posta").fill(USER.email);
    await page.locator("input[name=password]").fill("Dogru-Sifre-1");
    await page.getByRole("button", { name: "Giriş yap" }).click();
    await expect(page).toHaveURL("/pano");
  });
});

test.describe("sign-in form", () => {
  test("validates fields before calling Supabase", async ({ page }) => {
    let calls = 0;
    await mockSupabaseAuth(page, {
      token: (route) => {
        calls++;
        return route.abort();
      },
    });
    await page.goto("/giris");
    await page.getByRole("button", { name: "Giriş yap" }).click();
    const email = page.getByLabel("E-posta");
    await expect(page.getByText("E-posta adresinizi girin.")).toBeVisible();
    await expect(page.getByText("Şifrenizi girin.")).toBeVisible();
    await expect(email).toHaveAttribute("aria-invalid", "true");

    await email.fill("gecersiz-adres");
    await page.getByRole("button", { name: "Giriş yap" }).click();
    await expect(page.getByText("Geçerli bir e-posta adresi girin (ör. ad@firma.com).")).toBeVisible();
    expect(calls).toBe(0);
  });

  test("shows a Turkish message for wrong credentials", async ({ page }) => {
    await mockSupabaseAuth(page, {
      token: (route) =>
        route.fulfill({
          status: 400,
          contentType: "application/json",
          body: JSON.stringify({ code: "invalid_credentials", error_code: "invalid_credentials", msg: "Invalid login credentials" }),
        }),
    });
    await page.goto("/giris");
    await page.getByLabel("E-posta").fill(USER.email);
    await page.locator("input[name=password]").fill("Yanlis-Sifre-1");
    await page.getByRole("button", { name: "Giriş yap" }).click();
    await expect(page.getByRole("form", { name: "Giriş formu" }).getByRole("alert")).toHaveText(
      "E-posta adresi veya şifre hatalı.",
    );
    await expect(page).toHaveURL("/giris");
  });

  test("signs in and returns to the requested page with the real user in the header", async ({ page }) => {
    await mockSupabaseAuth(page);
    await mockApi(page);
    await page.goto("/urunler");
    await expect(page).toHaveURL("/giris?next=%2Furunler");
    await page.getByLabel("E-posta").fill(USER.email);
    await page.locator("input[name=password]").fill("Dogru-Sifre-1");
    await page.getByRole("button", { name: "Giriş yap" }).click();
    await expect(page).toHaveURL("/urunler");
    await expect(page.getByTestId("session-user")).toHaveText(USER.fullName);
  });
});

test.describe("signed in", () => {
  test("sign-in and sign-up pages send a signed-in user to the app", async ({ page }) => {
    await signedIn(page);
    await page.goto("/giris");
    await expect(page).toHaveURL("/pano");
    await page.goto("/kayit");
    await expect(page).toHaveURL("/pano");
  });

  test("sign-out clears the session and protects the app again", async ({ page }) => {
    await signedIn(page);
    await page.goto("/pano");
    await page.getByRole("button", { name: "Çıkış yap" }).click();
    await expect(page).toHaveURL("/giris");
    await page.goto("/pano");
    await expect(page).toHaveURL("/giris?next=%2Fpano");
  });

  test("an API 401 signs the user out and returns to sign-in", async ({ page }) => {
    await addSessionCookie(page.context());
    await mockSupabaseAuth(page);
    await page.route("http://localhost:5280/api/v1/me", (route) =>
      route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        headers: { "Access-Control-Allow-Origin": "*" },
        body: JSON.stringify({ status: 401, code: "auth.invalid_token" }),
      }),
    );
    await page.goto("/stok");
    await expect(page).toHaveURL("/giris?next=%2Fstok");
  });
});

test.describe("sign-up and password reset forms", () => {
  test("sign-up validates the password and its confirmation", async ({ page }) => {
    await page.goto("/kayit");
    await page.getByLabel("Ad soyad").fill("Ayşe Yılmaz");
    await page.getByLabel("E-posta").fill("ayse@example.test");
    await page.locator("input[name=password]").fill("kisa");
    await page.locator("input[name=confirm]").fill("baska");
    await page.getByRole("button", { name: "Hesap oluştur" }).click();
    await expect(page.getByText("Şifre en az 8 karakter olmalı.")).toBeVisible();
    await expect(page.getByText("Şifreler eşleşmiyor.")).toBeVisible();
  });

  test("sign-up with e-mail confirmation shows a check-your-inbox message", async ({ page }) => {
    await mockSupabaseAuth(page, {
      // Supabase answers with a user but no session while the address is unconfirmed.
      signup: (route) =>
        route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ id: USER.id, email: USER.email }) }),
    });
    await page.goto("/kayit");
    await page.getByLabel("Ad soyad").fill("Mock Kullanıcı");
    await page.getByLabel("E-posta").fill(USER.email);
    await page.locator("input[name=password]").fill("Guclu-Sifre-1");
    await page.locator("input[name=confirm]").fill("Guclu-Sifre-1");
    await page.getByRole("button", { name: "Hesap oluştur" }).click();
    await expect(page.getByRole("status")).toContainText(`${USER.email} adresine bir doğrulama bağlantısı gönderdik`);
  });

  test("password reset never reveals whether the account exists", async ({ page }) => {
    await mockSupabaseAuth(page);
    await page.goto("/sifre-sifirla");
    await page.getByRole("button", { name: "Sıfırlama bağlantısı gönder" }).click();
    await expect(page.getByText("E-posta adresinizi girin.")).toBeVisible();
    await page.getByLabel("E-posta").fill("kimse@example.test");
    await page.getByRole("button", { name: "Sıfırlama bağlantısı gönder" }).click();
    await expect(page.getByRole("status")).toContainText("Bu adrese kayıtlı bir hesap varsa");
  });
});
