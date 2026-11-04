/**
 * Customers (/musteriler), customer detail/edit (/musteriler/[id]), addresses, anonymize and dealer groups — #31 web.
 * MOCK: Supabase Auth and the BayiOS API (incl. in-memory Catalog, Inventory and Customers/Pricing) are faked with
 * page.route — support/mock-backend.ts, support/mock-commerce.ts. All data is synthetic; nothing here talks to a real
 * API or database.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockCatalog, fulfillJson, problemBody } from "./support/mock-catalog";
import { GROUP_IDS, createMockCommerce, customerId } from "./support/mock-commerce";

test.use({ viewport: DESKTOP });

const as = (role: string): MockMembership => ({ ...TENANT_A, role });

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

function setup(customers = 3) {
  const catalog = createMockCatalog({ products: 3 });
  const commerce = createMockCommerce(catalog, { customers });
  return { catalog, commerce };
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const requestsTo = (api: Api, method: string, path: string) => api.requests.filter((r) => r.method === method && r.path === path);

test.describe("müşteri listesi (mock)", () => {
  test("keyset sayfalama, maskeli vergi no, arama ve bayi grubu filtresi URL'de", async ({ page }) => {
    const { catalog, commerce } = setup(60);
    const api = await signedIn(page, { catalog, commerce });
    await page.goto("/musteriler");
    const table = page.getByRole("table", { name: "Müşteriler" });
    await expect(table.getByRole("row")).toHaveCount(51);
    await expect(page.getByText("50 müşteri gösteriliyor (devamı var)")).toBeVisible();
    // 11-digit tax numbers arrive masked from the list endpoint and are shown as returned; 10-digit VKN in clear.
    await expect(table.getByRole("row", { name: /Mock Müşteri 001/ })).toContainText("*******0001");
    await expect(table.getByRole("row", { name: /Mock Müşteri 002/ })).toContainText("9000000002");
    const first = new URLSearchParams(requestsTo(api, "GET", "/api/v1/customers")[0].search);
    expect(first.get("limit")).toBe("50");
    expect(first.has("cursor")).toBe(false);

    await page.getByRole("button", { name: "Daha fazla yükle" }).click();
    await expect(table.getByRole("row")).toHaveCount(61);
    await expect(page.getByText("60 müşteri gösteriliyor", { exact: true })).toBeVisible();
    expect(new URLSearchParams(requestsTo(api, "GET", "/api/v1/customers").at(-1)!.search).get("cursor")).toBe("o:50");

    await page.getByRole("searchbox", { name: "Müşteri ara" }).fill("Müşteri 007");
    await expect(page).toHaveURL(/q=M%C3%BC%C5%9Fteri\+007|q=Müşteri/);
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table).toContainText("Mock Müşteri 007");

    await page.getByRole("button", { name: "Aramayı ve filtreleri temizle" }).click();
    await page.getByRole("combobox", { name: "Bayi grubu" }).selectOption({ label: "Bayi A (mock)" });
    await expect(page).toHaveURL(new RegExp(`grup=${GROUP_IDS.bayiA}`));
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect.poll(() => new URLSearchParams(requestsTo(api, "GET", "/api/v1/customers").at(-1)!.search).get("dealerGroupId")).toBe(GROUP_IDS.bayiA);

    await page.getByRole("combobox", { name: "Durum" }).selectOption({ label: "Tümü (arşiv dahil)" });
    await expect.poll(() => new URLSearchParams(requestsTo(api, "GET", "/api/v1/customers").at(-1)!.search).get("status")).toBe("active,inactive,archived");
  });

  test("mobil: kart görünümü ve 'Yeni müşteri'", async ({ page }) => {
    await page.setViewportSize(MOBILE);
    const { catalog, commerce } = setup(3);
    await signedIn(page, { catalog, commerce });
    await page.goto("/musteriler");
    const cards = page.getByRole("list", { name: "Müşteriler" });
    await expect(cards.getByRole("listitem")).toHaveCount(3);
    await expect(cards.getByRole("link", { name: "Mock Müşteri 001" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Yeni müşteri" })).toBeVisible();
    await expectNoSeriousViolations(page);
  });
});

test.describe("müşteri oluşturma ve düzenleme (mock)", () => {
  test("oluştur → detay; düzenle merge patch + If-Match; 412 → yeniden yükle", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    const api = await signedIn(page, { catalog, commerce });
    await page.goto("/musteriler/yeni");
    await page.getByRole("button", { name: "Müşteriyi ekle" }).click();
    await expect(page.getByText("Müşteri kodu girin (ör. C-001).")).toBeVisible();

    await page.getByLabel("Müşteri kodu").fill("C-900");
    await page.getByLabel("Firma / müşteri adı").fill("Yeni Mock Ticaret");
    await page.getByLabel("Vergi no / TCKN").fill("1234567890");
    await page.getByLabel("Kredi limiti").fill("250000,50");
    await page.getByRole("combobox", { name: "Bayi grubu" }).selectOption({ label: "Bayi A (mock)" });
    await page.getByRole("button", { name: "Müşteriyi ekle" }).click();
    await expect(page).toHaveURL(/\/musteriler\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: "Yeni Mock Ticaret" })).toBeVisible();
    const create = requestsTo(api, "POST", "/api/v1/customers")[0];
    expect(create.headers["idempotency-key"]).toMatch(/[0-9a-f-]{36}/);
    expect(create.body).toMatchObject({ code: "C-900", name: "Yeni Mock Ticaret", creditLimit: "250000.50", dealerGroupId: GROUP_IDS.bayiA });

    // Edit: only the changed member, merge-patch, If-Match with the loaded version.
    // The /yeni page stays mounted but hidden (Activity) after the push: role queries only see the visible form.
    await page.getByRole("textbox", { name: "Telefon", exact: true }).fill("0212 555 0000");
    await page.getByRole("button", { name: "Kaydet" }).click();
    await expect(page.getByText("Müşteri kaydedildi")).toBeVisible();
    const id = new URL(page.url()).pathname.split("/").at(-1)!;
    const patch = requestsTo(api, "PATCH", `/api/v1/customers/${id}`)[0];
    expect(patch.body).toEqual({ phone: "0212 555 0000" });
    expect(patch.headers["if-match"]).toBe('W/"1"');
    expect(patch.headers["content-type"]).toContain("application/merge-patch+json");

    // Someone else saved in between: 412 → conflict banner, nothing overwritten, reload fetches the fresh version.
    commerce.customers.find((c) => c.id === id)!.version += 1;
    await page.getByRole("textbox", { name: "Yetkili kişi" }).fill("Mock Yetkili 2");
    await page.getByRole("button", { name: "Kaydet" }).click();
    await expect(page.getByText("Kayıt başkası tarafından değiştirildi")).toBeVisible();
    await page.getByRole("button", { name: "Yeniden yükle" }).click();
    await expect(page.getByText("Kayıt başkası tarafından değiştirildi")).toBeHidden();
    await expect(page.getByRole("textbox", { name: "Yetkili kişi" })).toHaveValue("");
  });

  test("422 alan hataları ilgili alana Türkçe yazılır", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    await signedIn(page, { catalog, commerce });
    commerce.overrides.set(`PATCH /api/v1/customers/${customerId(1)}`, (route) =>
      fulfillJson(route, 422, problemBody(422, "validation.failed", { errors: { email: ["Must be a valid e-mail address."], "attributes.x": ["Required."] } })),
    );
    await page.goto(`/musteriler/${customerId(1)}`);
    await page.getByLabel("E-posta").fill("mock@example.test");
    await page.getByRole("button", { name: "Kaydet" }).click();
    await expect(page.getByLabel("E-posta")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByText("Geçerli bir e-posta adresi girin.")).toBeVisible();
    // A key without an input lands in the summary.
    await expect(page.getByRole("alert").filter({ hasText: "Kaydedilemedi" })).toContainText("Bu alan zorunlu.");
  });
});

test.describe("adresler (mock)", () => {
  test("varsayılan adres değiştirme: PATCH isDefault + If-Match, eski varsayılan düşer", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    const api = await signedIn(page, { catalog, commerce });
    await page.goto(`/musteriler/${customerId(1)}`);
    const shipping = page.getByRole("list", { name: "Teslimat adresleri" });
    await expect(shipping.getByRole("listitem")).toHaveCount(2);
    await expect(shipping.getByRole("listitem").first()).toContainText("Merkez (mock)");
    await expect(shipping.getByRole("listitem").first()).toContainText("Varsayılan");

    await page.getByRole("button", { name: "Depo (mock) adresini varsayılan yap (teslimat)" }).click();
    await expect(page.getByText("Varsayılan adres değişti")).toBeVisible();
    await expect(shipping.getByRole("listitem").first()).toContainText("Depo (mock)");
    await expect(shipping.getByRole("listitem").first()).toContainText("Varsayılan");
    await expect(shipping.getByRole("listitem").nth(1)).toContainText("Merkez (mock)");
    await expect(shipping.getByRole("listitem").nth(1).getByRole("button", { name: /varsayılan yap/ })).toBeVisible();
    const call = api.requests.find((r) => r.method === "PATCH" && r.path.includes("/addresses/"))!;
    expect(call.body).toEqual({ isDefault: true });
    expect(call.headers["if-match"]).toBe('W/"1"');

    // New billing address: first of its type becomes default.
    await page.getByRole("button", { name: "Adres ekle" }).click();
    const dialog = page.getByRole("dialog", { name: /Yeni adres/ });
    await dialog.getByRole("combobox", { name: "Adres türü" }).selectOption("billing");
    await dialog.getByLabel("Adres satırı 1").fill("Fatura Mah. Mock Sk. 3");
    await dialog.getByRole("textbox", { name: "İl", exact: true }).fill("Ankara");
    await dialog.getByRole("button", { name: "Adresi ekle" }).click();
    const billing = page.getByRole("list", { name: "Fatura adresleri" });
    await expect(billing.getByRole("listitem")).toHaveCount(1);
    await expect(billing.getByRole("listitem").first()).toContainText("Varsayılan");
    expect(requestsTo(api, "POST", `/api/v1/customers/${customerId(1)}/addresses`)[0].headers["idempotency-key"]).toBeTruthy();
    await expectNoSeriousViolations(page);
  });
});

test.describe("KVKK anonimleştirme (mock)", () => {
  test("yalnızca owner/admin görür; müşteri adı yazılmadan onay düğmesi kapalı", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    const api = await signedIn(page, { catalog, commerce });
    await page.goto(`/musteriler/${customerId(1)}`);
    await page.getByRole("button", { name: "Müşteriyi anonimleştir" }).click();
    const dialog = page.getByRole("alertdialog", { name: "Müşteri anonimleştirilsin mi?" });
    const confirm = dialog.getByRole("button", { name: "Kalıcı olarak anonimleştir" });
    await expect(confirm).toBeDisabled();
    await dialog.getByRole("textbox").fill("Mock Müşteri");
    await expect(confirm).toBeDisabled();
    await expectNoSeriousViolations(page);
    await dialog.getByRole("textbox").fill("mock müşteri 001");
    await expect(confirm).toBeEnabled();
    await confirm.click();
    await expect(page.getByText("Müşteri anonimleştirildi", { exact: true }).first()).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(/Silinmiş müşteri/);
    await expect(page.getByRole("heading", { name: "Adresler" })).toBeHidden();
    await expect(page.getByLabel("Firma / müşteri adı")).toBeDisabled();
    const call = requestsTo(api, "POST", `/api/v1/customers/${customerId(1)}/anonymize`)[0];
    expect(call.headers["idempotency-key"]).toBeTruthy();
  });

  test("manager anonimleştirme bölümünü görmez", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    await signedIn(page, { catalog, commerce, memberships: [as("manager")] });
    await page.goto(`/musteriler/${customerId(1)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Mock Müşteri 001" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Müşteriyi anonimleştir" })).toHaveCount(0);
  });
});

test.describe("rol görünürlüğü (mock)", () => {
  test("viewer: salt okunur liste ve detay, düzenleme düğmesi yok", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    const api = await signedIn(page, { catalog, commerce, memberships: [as("viewer")] });
    await page.goto("/musteriler");
    await expect(page.getByRole("table", { name: "Müşteriler" }).getByRole("row")).toHaveCount(2);
    await expect(page.getByRole("link", { name: "Yeni müşteri" })).toHaveCount(0);
    await page.getByRole("link", { name: "Mock Müşteri 001" }).first().click();
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByLabel("Firma / müşteri adı")).toBeDisabled();
    await expect(page.getByRole("button", { name: "Kaydet" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Adres ekle" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /varsayılan yap/ })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Arşivle" })).toHaveCount(0);
    expect(api.requests.filter((r) => r.method !== "GET")).toEqual([]);
  });

  test("sales: müşteri düzenler ama bayi grubu / fiyat listesi atayamaz", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    await signedIn(page, { catalog, commerce, memberships: [as("sales")] });
    await page.goto(`/musteriler/${customerId(1)}`);
    await expect(page.getByLabel("Firma / müşteri adı")).toBeEnabled();
    await expect(page.getByRole("combobox", { name: "Bayi grubu" })).toBeDisabled();
    await expect(page.getByRole("combobox", { name: "Müşteriye özel fiyat listesi" })).toBeDisabled();
    await expect(page.getByText("Bayi grubu ve fiyat listesini işletme sahibi, yönetici veya operasyon müdürü atayabilir.")).toBeVisible();
  });

  test("arşivle: onay sonrası salt okunur, arşivden çıkar geri açar", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    const api = await signedIn(page, { catalog, commerce });
    await page.goto(`/musteriler/${customerId(1)}`);
    await page.getByRole("button", { name: "Arşivle" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Arşivle" }).click();
    await expect(page.getByText("Arşivlenmiş müşteri")).toBeVisible();
    await expect(page.getByLabel("Firma / müşteri adı")).toBeDisabled();
    expect(requestsTo(api, "PATCH", `/api/v1/customers/${customerId(1)}`).at(-1)!.body).toEqual({ status: "archived" });
    await page.getByRole("button", { name: "Arşivden çıkar" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Arşivden çıkar" }).click();
    await expect(page.getByLabel("Firma / müşteri adı")).toBeEnabled();
  });
});

test.describe("bayi grupları (mock)", () => {
  test("grup oluştur (fiyat listesi + iskonto) ve düzenle", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    const api = await signedIn(page, { catalog, commerce });
    await page.goto("/musteriler/gruplar");
    const table = page.getByRole("table", { name: "Bayi grupları" });
    await expect(table.getByRole("row", { name: /Bayi A \(mock\)/ })).toContainText("Bayi A Listesi (mock)");
    await expect(table.getByRole("row", { name: /Bayi A \(mock\)/ })).toContainText("%5");

    await page.getByRole("button", { name: "Yeni grup" }).click();
    const dialog = page.getByRole("dialog", { name: "Yeni bayi grubu" });
    await dialog.getByLabel("Grup adı").fill("Bayi B (mock)");
    await dialog.getByRole("combobox", { name: "Fiyat listesi" }).selectOption({ label: "Perakende (mock)" });
    await dialog.getByLabel("Varsayılan iskonto (%)").fill("7,5");
    await dialog.getByRole("button", { name: "Grubu ekle" }).click();
    await expect(table.getByRole("row", { name: /Bayi B \(mock\)/ })).toContainText("%7,5");
    expect(requestsTo(api, "POST", "/api/v1/dealer-groups")[0].body).toMatchObject({ name: "Bayi B (mock)", defaultDiscountPercent: "7.5" });

    await page.getByRole("button", { name: "Bayi A (mock) grubunu düzenle" }).click();
    const edit = page.getByRole("dialog", { name: /Grubu düzenle/ });
    await edit.getByLabel("Varsayılan iskonto (%)").fill("6");
    await edit.getByRole("button", { name: "Kaydet" }).click();
    await expect(table.getByRole("row", { name: /Bayi A \(mock\)/ })).toContainText("%6");
    const patch = requestsTo(api, "PATCH", `/api/v1/dealer-groups/${GROUP_IDS.bayiA}`)[0];
    expect(patch.body).toEqual({ defaultDiscountPercent: "6" });
    expect(patch.headers["if-match"]).toBe('W/"1"');
    await expectNoSeriousViolations(page);
  });

  test("sales: salt okunur", async ({ page }) => {
    const { catalog, commerce } = setup(1);
    await signedIn(page, { catalog, commerce, memberships: [as("sales")] });
    await page.goto("/musteriler/gruplar");
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByRole("button", { name: "Yeni grup" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /grubunu düzenle/ })).toHaveCount(0);
  });
});

test.describe("axe — müşteri sayfaları", () => {
  for (const [name, viewport] of [
    ["desktop", DESKTOP],
    ["mobile", MOBILE],
  ] as const) {
    test(`${name}: liste, yeni, detay`, async ({ page }) => {
      await page.setViewportSize(viewport);
      const { catalog, commerce } = setup(3);
      await signedIn(page, { catalog, commerce });
      for (const route of ["/musteriler", "/musteriler/yeni", `/musteriler/${customerId(1)}`]) {
        await page.goto(route);
        await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
        await expect(page.getByText("Yükleniyor…")).toHaveCount(0);
        await expectNoSeriousViolations(page);
      }
    });
  }
});
