/**
 * Sign-up → onboarding → dashboard, tenant picker and role-based menu.
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts).
 * The same flow against the real local stack is e2e-local/real-stack.spec.ts.
 */
import { expect, test, type Route } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { TENANT_A, TENANT_B, USER, mockApi, mockSupabaseAuth, problem, signedIn, type MockMembership } from "./support/mock-backend";

test.use({ viewport: DESKTOP });

const NEW_TENANT: MockMembership = {
  tenantId: "00000000-0000-4000-8000-0000000000c3",
  tenantName: "Yıldız Toptan Gıda",
  tenantSlug: "yildiz-toptan-gida",
  role: "owner",
  status: "active",
};

const created = (route: Route) =>
  route.fulfill({
    status: 201,
    contentType: "application/json",
    headers: { "Access-Control-Allow-Origin": "*", Location: "/api/v1/tenant" },
    body: JSON.stringify({
      id: NEW_TENANT.tenantId,
      name: NEW_TENANT.tenantName,
      slug: NEW_TENANT.tenantSlug,
      status: "active",
      sector: "led_electronics",
      defaultCurrency: "TRY",
      locale: "tr-TR",
      timeZone: "Europe/Istanbul",
      role: "owner",
    }),
  });

test("kayıt → onboarding → pano (mock): slug conflict is retried with the same Idempotency-Key", async ({ page }) => {
  await mockSupabaseAuth(page); // signup returns a session (e-mail confirmation off in this mock)
  let attempts = 0;
  const api = await mockApi(page, {
    memberships: [],
    createTenant: (route) => {
      attempts++;
      if (attempts === 1) {
        return route.fulfill({
          status: 409,
          contentType: "application/problem+json",
          headers: { "Access-Control-Allow-Origin": "*" },
          body: JSON.stringify(problem(409, "tenant.slug_taken")),
        });
      }
      api.memberships = [NEW_TENANT];
      return created(route);
    },
  });

  await page.goto("/kayit");
  await page.getByLabel("Ad soyad").fill(USER.fullName);
  await page.getByLabel("E-posta").fill(USER.email);
  await page.locator("input[name=password]").fill("Guclu-Sifre-1");
  await page.locator("input[name=confirm]").fill("Guclu-Sifre-1");
  await page.getByRole("button", { name: "Hesap oluştur" }).click();

  await expect(page).toHaveURL("/isletme-olustur");
  await expect(page.getByRole("heading", { level: 1, name: "İşletmenizi oluşturun" })).toBeVisible();

  // Step 1 — validation, then name; the slug is derived with Turkish transliteration.
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByText("İşletme adını girin.")).toBeVisible();
  await page.getByLabel("İşletme adı").fill("Yıldız Toptan Gıda");
  await expect(page.getByLabel("İşletme adresi (kısa ad)")).toHaveValue("yildiz-toptan-gida");
  await page.getByRole("button", { name: "Devam" }).click();

  // Step 2 — starter template: sector-agnostic, the plain start is pre-selected; an example template is optional.
  await expect(page.getByRole("heading", { level: 2, name: "Ürün şablonu" })).toBeFocused();
  await expect(page.getByLabel(/Sade başlangıç/)).toBeChecked();
  await expect(page.getByText(/motosiklet|LED/i)).toHaveCount(0);
  await page.getByLabel(/Teknik özellikli ürünler/).check();
  await page.getByRole("button", { name: "Devam" }).click();

  // Step 3 — commercial settings.
  await expect(page.getByLabel("Para birimi")).toHaveValue("TRY");
  await expect(page.getByLabel("Varsayılan KDV oranı")).toHaveValue("20");
  await page.getByLabel("Para birimi").selectOption("EUR");
  await page.getByLabel("Varsayılan KDV oranı").selectOption("10");
  await page.getByLabel("Fiyat gösterimi").selectOption("included");
  // OD-09: negative stock is a fixed rule, shown but not selectable.
  await expect(page.getByText("Stok eksiye düşmez")).toBeVisible();
  await expect(page.getByLabel("Negatif stok")).toHaveCount(0);
  await page.getByRole("button", { name: "Devam" }).click();

  // Step 4 — summary and create (first attempt: slug taken).
  await expect(page.getByText("yildiz-toptan-gida")).toBeVisible();
  await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "İşletme bilgileri" })).toBeVisible();
  await expect(page.getByText("Bu işletme adresi başka bir işletme tarafından kullanılıyor.", { exact: false })).toBeVisible();

  await page.getByLabel("İşletme adresi (kısa ad)").fill("yildiz-toptan-2");
  for (let i = 0; i < 3; i++) await page.getByRole("button", { name: "Devam" }).click();
  await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();

  // #83: the guided setup follows the business creation.
  await expect(page).toHaveURL("/kurulum");
  await expect(page.getByRole("heading", { level: 1, name: "İşletmenizi kuralım" })).toBeVisible();
  await expect(page.getByTitle("Aktif işletme")).toHaveText(NEW_TENANT.tenantName);

  const posts = api.requests.filter((r) => r.method === "POST" && r.path === "/api/v1/tenants");
  expect(posts).toHaveLength(2);
  expect(posts[0].headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
  expect(posts[1].headers["idempotency-key"]).toBe(posts[0].headers["idempotency-key"]);
  expect(posts[0].headers["authorization"]).toMatch(/^Bearer /);
  expect(posts[1].body).toEqual({ name: "Yıldız Toptan Gıda", slug: "yildiz-toptan-2", sector: "led_electronics" });

  // Sector template applied by the client right after creation, as the new owner (Identity cannot call Catalog).
  const apply = api.requests.filter((r) => r.method === "POST" && r.path === "/api/v1/sector-templates/led_electronics/apply");
  expect(apply).toHaveLength(1);
  expect(apply[0].headers["x-tenant-id"]).toBe(NEW_TENANT.tenantId);
  expect(apply[0].headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);

  const patch = api.requests.find((r) => r.method === "PATCH" && r.path === "/api/v1/tenant/settings");
  expect(patch?.headers["x-tenant-id"]).toBe(NEW_TENANT.tenantId);
  expect(patch?.headers["if-match"]).toBe('W/"0"');
  expect(patch?.body).toEqual({ defaultTaxRate: 10, pricesIncludeTaxDisplay: true });

  const currency = api.requests.find((r) => r.method === "PATCH" && r.path === "/api/v1/tenant");
  expect(currency?.headers["if-match"]).toBe('W/"0"');
  expect(currency?.body).toEqual({ defaultCurrency: "EUR" });
});

test("a failed template apply is retried with the same key, without creating a second tenant", async ({ page }) => {
  let applyCalls = 0;
  const api = await signedIn(page, {
    memberships: [],
    createTenant: (route) => {
      api.memberships = [NEW_TENANT];
      return created(route);
    },
    applyTemplate: (route) => {
      applyCalls++;
      if (applyCalls === 1) return route.abort("connectionreset");
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        headers: { "Access-Control-Allow-Origin": "*" },
        body: JSON.stringify({ template: "general", unitsCreated: [], categoriesCreated: [], fieldsCreated: [], skipped: 3 }),
      });
    },
  });
  await page.goto("/isletme-olustur");
  await page.getByLabel("İşletme adı").fill("Deneme Toptan");
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByLabel(/Sade başlangıç/)).toBeChecked(); // default: no industry to pick
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();

  await expect(page.getByRole("alert").filter({ hasText: "İşletmeniz oluşturuldu, ancak ürün şablonu" })).toBeVisible();
  await page.getByRole("button", { name: "Kalan adımları tamamla" }).click();
  await expect(page).toHaveURL("/kurulum");

  expect(api.requests.filter((r) => r.method === "POST" && r.path === "/api/v1/tenants")).toHaveLength(1);
  const applies = api.requests.filter((r) => r.path.endsWith("/apply"));
  expect(applies).toHaveLength(2);
  expect(applies[1].headers["idempotency-key"]).toBe(applies[0].headers["idempotency-key"]);
  // Defaults chosen → no settings/currency PATCH needed.
  expect(api.requests.some((r) => r.method === "PATCH")).toBe(false);
});

test("a user without memberships is sent from the app to onboarding", async ({ page }) => {
  await signedIn(page, { memberships: [] });
  await page.goto("/pano");
  await expect(page).toHaveURL("/isletme-olustur");
});

test("several memberships: picker, X-Tenant-Id selection and header switcher", async ({ page }) => {
  const api = await signedIn(page, { memberships: [TENANT_A, TENANT_B] });
  await page.goto("/pano");
  await expect(page).toHaveURL("/isletme-sec");
  await page.getByRole("button", { name: TENANT_B.tenantName }).click();
  await expect(page).toHaveURL("/pano");

  // warehouse_staff: no Pano/Müşteriler/Raporlar/Ayarlar in the menu (visibility only; the API authorizes).
  const nav = page.getByRole("navigation", { name: "Ana menü" });
  await expect(nav.getByRole("link", { name: "Depo" })).toBeVisible();
  for (const hidden of ["Pano", "Müşteriler", "Raporlar", "Ayarlar"]) {
    await expect(nav.getByRole("link", { name: hidden, exact: true })).toHaveCount(0);
  }
  await expect(page.getByRole("banner").getByText("Depo personeli")).toBeVisible();

  const switcher = page.getByLabel("Aktif işletme");
  await expect(switcher).toHaveValue(TENANT_B.tenantId);
  await switcher.selectOption(TENANT_A.tenantId);
  await expect(nav.getByRole("link", { name: "Ayarlar" })).toBeVisible();
  await expect(page.getByRole("banner").getByText("İşletme sahibi")).toBeVisible();

  // The selection survives a reload (stored per user, sent as X-Tenant-Id by the API client).
  await page.reload();
  await expect(page.getByLabel("Aktif işletme")).toHaveValue(TENANT_A.tenantId);
  expect(api.requests.some((r) => r.path === "/api/v1/me")).toBe(true);
});
