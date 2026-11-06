/**
 * Marka / kategori / birim screens (/urunler/markalar, /urunler/kategoriler, /urunler/birimler — #129 item 2).
 * MOCK: Supabase Auth and the BayiOS API (in-memory Catalog incl. units/brands/categories CRUD) are faked with
 * page.route — support/mock-backend.ts, support/mock-catalog.ts. All data is synthetic.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { BRAND_IDS, CATEGORY_IDS, createMockCatalog, fulfillJson, problemBody } from "./support/mock-catalog";

const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };
const SALES: MockMembership = { ...TENANT_A, role: "sales" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

type Api = Awaited<ReturnType<typeof signedIn>>;
const writes = (api: Api, method: string, prefix: string) => api.requests.filter((r) => r.method === method && r.path.startsWith(prefix));

test.describe("markalar (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("ekle (Idempotency-Key), aynı ad 409 alanda, yeniden adlandır (If-Match), arşivle; arşivlenenler filtresi", async ({ page }) => {
    const catalog = createMockCatalog();
    const api = await signedIn(page, { catalog });
    await page.goto("/urunler");
    await page.getByRole("link", { name: "Marka, kategori ve birimler" }).click();
    await expect(page).toHaveURL(/\/urunler\/markalar$/);
    await expect(page.getByRole("heading", { level: 1, name: "Markalar" })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Katalog tanımları" }).getByRole("link", { name: "Markalar" })).toHaveAttribute("aria-current", "page");
    const table = page.getByRole("table", { name: "Markalar" });
    await expect(table.getByRole("row")).toHaveCount(3);

    // Create.
    await page.getByRole("button", { name: "Yeni marka" }).first().click();
    const dialog = page.getByRole("dialog", { name: "Yeni marka" });
    await dialog.getByLabel("Marka adı").fill("  Yıldız   Motor ");
    await dialog.getByRole("button", { name: "Markayı ekle" }).click();
    await expect(dialog).toHaveCount(0);
    await expect(table.getByRole("row", { name: /Yıldız Motor/ })).toBeVisible();
    const post = writes(api, "POST", "/api/v1/brands")[0];
    expect(post.headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(post.body).toEqual({ name: "Yıldız Motor" });

    // Duplicate name (case-insensitive) → 409 on the field; nothing added.
    await page.getByRole("button", { name: "Yeni marka" }).first().click();
    await page.getByRole("dialog", { name: "Yeni marka" }).getByLabel("Marka adı").fill("simya (MOCK)");
    await page.getByRole("dialog", { name: "Yeni marka" }).getByRole("button", { name: "Markayı ekle" }).click();
    await expect(page.getByRole("dialog", { name: "Yeni marka" }).getByText("Bu marka adı zaten kayıtlı.")).toBeVisible();
    await expect(page.getByRole("dialog", { name: "Yeni marka" }).getByLabel("Marka adı")).toHaveAttribute("aria-invalid", "true");
    await page.getByRole("dialog", { name: "Yeni marka" }).getByRole("button", { name: "Vazgeç" }).click();

    // Rename with If-Match.
    await page.getByRole("button", { name: "Parlak (mock) markasını yeniden adlandır" }).click();
    const rename = page.getByRole("dialog", { name: /Markayı yeniden adlandır/ });
    await rename.getByLabel("Marka adı").fill("Parlak Aydınlatma");
    await rename.getByRole("button", { name: "Kaydet" }).click();
    await expect(table.getByRole("row", { name: /Parlak Aydınlatma/ })).toBeVisible();
    const patch = writes(api, "PATCH", `/api/v1/brands/${BRAND_IDS.parlak}`)[0];
    expect(patch.headers["if-match"]).toBe('W/"1"');
    expect(patch.headers["content-type"]).toContain("application/merge-patch+json");

    // Archive (confirmation) → hidden, visible again with the filter.
    await page.getByRole("button", { name: "Simya (mock) markasını arşivle" }).click();
    const confirm = page.getByRole("alertdialog", { name: "Markayı arşivle" });
    await expect(confirm).toContainText("Bu markadaki ürünler değişmez");
    await confirm.getByRole("button", { name: "Arşivle" }).click();
    await expect(table.getByRole("row", { name: /Simya/ })).toHaveCount(0);
    expect(writes(api, "DELETE", `/api/v1/brands/${BRAND_IDS.sim}`)[0].headers["if-match"]).toBe('W/"1"');
    await page.getByLabel("Arşivlenenleri göster").check();
    await expect(table.getByRole("row", { name: /Simya/ })).toContainText("Arşivlenmiş");
    await expect(page.getByRole("button", { name: "Simya (mock) markasını arşivle" })).toHaveCount(0);
  });

  test("sunucu araması (q) ve 412: başkası değiştirdiyse kaydetmez, yenilemeyi söyler", async ({ page }) => {
    const catalog = createMockCatalog();
    const api = await signedIn(page, { catalog });
    await page.goto("/urunler/markalar");
    await page.getByRole("searchbox", { name: "Marka ara" }).fill("parl");
    await page.getByRole("button", { name: "Ara", exact: true }).click();
    const table = page.getByRole("table", { name: "Markalar" });
    await expect(table.getByRole("row")).toHaveCount(2);
    expect(api.requests.some((r) => r.path === "/api/v1/brands" && new URLSearchParams(r.search).get("q") === "parl")).toBe(true);
    await page.getByRole("searchbox", { name: "Marka ara" }).fill("yok-böyle");
    await page.getByRole("button", { name: "Ara", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Aramaya uyan marka yok" })).toBeVisible();
    await page.getByRole("searchbox", { name: "Marka ara" }).fill("");
    await expect(table.getByRole("row")).toHaveCount(3);

    // Someone else renamed the brand meanwhile.
    catalog.brands[1].version = 2;
    await page.getByRole("button", { name: "Parlak (mock) markasını yeniden adlandır" }).click();
    const rename = page.getByRole("dialog", { name: /Markayı yeniden adlandır/ });
    await rename.getByLabel("Marka adı").fill("Parlak 2");
    await rename.getByRole("button", { name: "Kaydet" }).click();
    await expect(rename.getByText(/başka biri tarafından değiştirildi/)).toBeVisible();
    expect(catalog.brands[1].name).toBe("Parlak (mock)");
  });

  test("operasyon müdürü marka ekler ama birim/kategoriyi yalnızca görür; satış her şeyi yalnızca görür", async ({ page }) => {
    await signedIn(page, { memberships: [MANAGER] });
    await page.goto("/urunler/markalar");
    await expect(page.getByRole("button", { name: "Yeni marka" }).first()).toBeVisible();
    await page.getByRole("navigation", { name: "Katalog tanımları" }).getByRole("link", { name: "Birimler" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Birimler" })).toBeVisible();
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByRole("button", { name: "Yeni birim" })).toHaveCount(0);
    await page.getByRole("navigation", { name: "Katalog tanımları" }).getByRole("link", { name: "Kategoriler" }).click();
    await expect(page.getByRole("table", { name: "Kategori ağacı" }).getByRole("row")).toHaveCount(4);
    await expect(page.getByRole("button", { name: "Yeni ana kategori" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /alt kategori ekle/ })).toHaveCount(0);
  });

  test("satış: salt okunur marka listesi", async ({ page }) => {
    await signedIn(page, { memberships: [SALES] });
    await page.goto("/urunler/markalar");
    await expect(page.getByRole("table", { name: "Markalar" }).getByRole("row")).toHaveCount(3);
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByRole("button", { name: /Yeni marka|yeniden adlandır|arşivle/ })).toHaveCount(0);
  });

  test("liste yüklenemezse hata ve Tekrar dene", async ({ page }) => {
    const catalog = createMockCatalog();
    catalog.overrides.set("GET /api/v1/brands", (route) => fulfillJson(route, 500, problemBody(500, "server.error")));
    await signedIn(page, { catalog });
    await page.goto("/urunler/markalar");
    await expect(page.getByRole("heading", { name: "Markalar yüklenemedi" })).toBeVisible();
    await page.getByRole("button", { name: "Tekrar dene" }).click();
    await expect(page.getByRole("table", { name: "Markalar" }).getByRole("row")).toHaveCount(3);
  });
});

test.describe("kategoriler (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("ağaç; ana ve alt kategori ekle; kardeşte aynı ad 409; alt kategorisi olan arşivlenemez; yeniden adlandır", async ({ page }) => {
    const catalog = createMockCatalog();
    const api = await signedIn(page, { catalog });
    await page.goto("/urunler/kategoriler");
    const table = page.getByRole("table", { name: "Kategori ağacı" });
    // Depth-first: Aydınlatma › Panel, then Motor.
    await expect(table.getByRole("row").nth(1)).toContainText("Aydınlatma");
    await expect(table.getByRole("row").nth(2)).toContainText("Panel");
    await expect(table.getByRole("row").nth(2)).toContainText("seviye 2");
    await expect(table.getByRole("row").nth(3)).toContainText("Motor");

    // Child under Panel.
    await page.getByRole("button", { name: "Panel: alt kategori ekle" }).click();
    const child = page.getByRole("dialog", { name: "Panel altına kategori" });
    await expect(child).toContainText("Üst kategori: Aydınlatma › Panel");
    await child.getByLabel("Kategori adı").fill("60x60");
    await child.getByRole("button", { name: "Kategoriyi ekle" }).click();
    await expect(table.getByRole("row").nth(3)).toContainText("60x60");
    const post = writes(api, "POST", "/api/v1/categories")[0];
    expect(post.body).toEqual({ name: "60x60", parentId: CATEGORY_IDS.panel, sortOrder: 0 });
    expect(post.headers["idempotency-key"]).toBeTruthy();

    // Same name among siblings → 409 on the name.
    await page.getByRole("button", { name: "Aydınlatma: alt kategori ekle" }).click();
    const dup = page.getByRole("dialog", { name: "Aydınlatma altına kategori" });
    await dup.getByLabel("Kategori adı").fill("panel");
    await dup.getByRole("button", { name: "Kategoriyi ekle" }).click();
    await expect(dup.getByText("Aynı üst kategoride bu adla bir kategori var. Farklı bir ad girin.")).toBeVisible();
    await dup.getByRole("button", { name: "Vazgeç" }).click();

    // A new root.
    await page.getByRole("button", { name: "Yeni ana kategori" }).click();
    const root = page.getByRole("dialog", { name: "Yeni ana kategori" });
    await root.getByLabel("Kategori adı").fill("Fren");
    await root.getByLabel("Sıra").fill("5");
    await root.getByRole("button", { name: "Kategoriyi ekle" }).click();
    await expect(table.getByRole("row").last()).toContainText("Fren");

    // Archive is blocked while active children exist (no request sent).
    await page.getByRole("button", { name: "Aydınlatma kategorisini arşivle" }).click();
    const blocked = page.getByRole("alertdialog", { name: "Kategoriyi arşivle" });
    await expect(blocked).toContainText("1 aktif alt kategorisi var");
    await expect(blocked.getByRole("button", { name: "Arşivle" })).toHaveCount(0);
    await blocked.getByRole("button", { name: "Tamam" }).click();
    expect(writes(api, "DELETE", "/api/v1/categories").length).toBe(0);

    // Rename + re-order with If-Match.
    await page.getByRole("button", { name: "Motor kategorisini düzenle" }).click();
    const edit = page.getByRole("dialog", { name: "Kategoriyi düzenle: Motor" });
    await edit.getByLabel("Kategori adı").fill("Motor parçaları");
    await edit.getByRole("button", { name: "Kaydet" }).click();
    await expect(table.getByRole("row", { name: /Motor parçaları/ })).toBeVisible();
    const patch = writes(api, "PATCH", `/api/v1/categories/${CATEGORY_IDS.motor}`)[0];
    expect(patch.body).toEqual({ name: "Motor parçaları" });
    expect(patch.headers["if-match"]).toBe('W/"1"');

    // Archive a leaf.
    await page.getByRole("button", { name: "60x60 kategorisini arşivle" }).click();
    await page.getByRole("alertdialog", { name: "Kategoriyi arşivle" }).getByRole("button", { name: "Arşivle" }).click();
    await expect(table.getByRole("row", { name: /60x60/ })).toHaveCount(0);
  });

  test("4. seviyenin altına ekleme sunulmaz", async ({ page }) => {
    const catalog = createMockCatalog();
    const NOW = "2026-11-01T09:00:00Z";
    catalog.categories.push(
      { id: "00000000-0000-4000-8000-c00000000041", parentId: CATEGORY_IDS.panel, name: "Üçüncü", path: "/x/", depth: 3, sortOrder: 0, isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
      { id: "00000000-0000-4000-8000-c00000000042", parentId: "00000000-0000-4000-8000-c00000000041", name: "Dördüncü", path: "/y/", depth: 4, sortOrder: 0, isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
    );
    await signedIn(page, { catalog });
    await page.goto("/urunler/kategoriler");
    await expect(page.getByRole("button", { name: "Üçüncü: alt kategori ekle" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Dördüncü kategorisini düzenle" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Dördüncü: alt kategori ekle" })).toHaveCount(0);
  });
});

test.describe("birimler (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("küsurat bayrağıyla ekle; aynı kod 409; düzenlemede kod salt okunur; arşivle", async ({ page }) => {
    const catalog = createMockCatalog();
    const api = await signedIn(page, { catalog });
    await page.goto("/urunler/birimler");
    const table = page.getByRole("table", { name: "Birimler" });
    await expect(table.getByRole("row", { name: /ADET/ })).toContainText("Hayır (tam sayı)");

    await page.getByRole("button", { name: "Yeni birim" }).first().click();
    const dialog = page.getByRole("dialog", { name: "Yeni birim" });
    await dialog.getByLabel("Kod").fill("kg");
    await dialog.getByLabel("Birim adı").fill("Kilogram");
    await dialog.getByLabel("Küsuratlı miktar kabul eder").check();
    await dialog.getByRole("button", { name: "Birimi ekle" }).click();
    await expect(table.getByRole("row", { name: /Kilogram/ })).toContainText("Evet");
    expect(writes(api, "POST", "/api/v1/units")[0].body).toEqual({ code: "kg", name: "Kilogram", allowsFraction: true });

    await page.getByRole("button", { name: "Yeni birim" }).first().click();
    const dup = page.getByRole("dialog", { name: "Yeni birim" });
    await dup.getByLabel("Kod").fill("adet");
    await dup.getByLabel("Birim adı").fill("Adet 2");
    await dup.getByRole("button", { name: "Birimi ekle" }).click();
    await expect(dup.getByText(/Bu birim kodu zaten kayıtlı/)).toBeVisible();
    await dup.getByLabel("Kod").fill("a b");
    await dup.getByRole("button", { name: "Birimi ekle" }).click();
    await expect(dup.getByText("Boşluk içermeyen 1–20 karakter girin.")).toBeVisible();
    await dup.getByRole("button", { name: "Vazgeç" }).click();

    await page.getByRole("button", { name: "Kilogram birimini düzenle" }).click();
    const edit = page.getByRole("dialog", { name: "Birimi düzenle: Kilogram" });
    await expect(edit.getByLabel("Kod")).toHaveAttribute("readonly", "");
    await edit.getByLabel("Küsuratlı miktar kabul eder").uncheck();
    await edit.getByRole("button", { name: "Kaydet" }).click();
    await expect(table.getByRole("row", { name: /Kilogram/ })).toContainText("Hayır");
    expect(writes(api, "PATCH", "/api/v1/units/")[0].body).toEqual({ allowsFraction: false });

    await page.getByRole("button", { name: "Kilogram birimini arşivle" }).click();
    await page.getByRole("alertdialog", { name: "Birimi arşivle" }).getByRole("button", { name: "Arşivle" }).click();
    await expect(table.getByRole("row", { name: /Kilogram/ })).toHaveCount(0);
  });
});

test.describe("ürün formu bağlantıları (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("birim yoksa Birimler ekranına bağlantı; '+ Yeni marka' formdan ekler ve seçer", async ({ page }) => {
    const catalog = createMockCatalog();
    catalog.units = [];
    await signedIn(page, { catalog });
    await page.goto("/urunler/yeni");
    await expect(page.getByText("Tanımlı birim yok.")).toBeVisible();
    await expect(page.getByRole("link", { name: "Birimler", exact: true })).toHaveAttribute("href", "/urunler/birimler");

    await page.getByRole("button", { name: "Yeni marka" }).click();
    const dialog = page.getByRole("dialog", { name: "Yeni marka" });
    await dialog.getByLabel("Marka adı").fill("Formdan Marka");
    await dialog.getByRole("button", { name: "Markayı ekle" }).click();
    await expect(dialog).toHaveCount(0);
    const select = page.getByLabel("Marka", { exact: true });
    await expect(select.locator("option:checked")).toHaveText("Formdan Marka");
  });
});

test.describe("katalog tanımları — mobil ve erişilebilirlik (mock)", () => {
  for (const [name, viewport] of [
    ["desktop", DESKTOP],
    ["mobile", MOBILE],
  ] as const) {
    test(`${name}: üç sekme ve açık diyalog axe ihlali yok`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await signedIn(page);
      for (const path of ["/urunler/markalar", "/urunler/kategoriler", "/urunler/birimler"]) {
        await page.goto(path);
        await expect(page.getByRole("table")).toBeVisible();
        await expect(page.getByRole("table").getByRole("row").nth(1)).toBeVisible();
        await expectNoSeriousViolations(page);
      }
      await page.getByRole("button", { name: "Yeni birim" }).first().click();
      await expect(page.getByRole("dialog", { name: "Yeni birim" })).toBeVisible();
      await expectNoSeriousViolations(page);
      if (name === "mobile") await page.screenshot({ path: "test-results/screens/birimler-dialog-mobile.png", fullPage: true });
    });
  }
});
