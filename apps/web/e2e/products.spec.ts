/**
 * Product list / create / edit and custom-field settings (#18).
 * MOCK: Supabase Auth and the BayiOS API (incl. an in-memory Catalog) are faked with page.route —
 * support/mock-backend.ts + support/mock-catalog.ts. All data is synthetic.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { CATEGORY_IDS, createMockCatalog, fulfillJson, makeProduct, problemBody, productId } from "./support/mock-catalog";

test.use({ viewport: DESKTOP });

const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const productRequests = (api: Awaited<ReturnType<typeof signedIn>>) =>
  api.requests.filter((r) => r.method === "GET" && r.path === "/api/v1/products").map((r) => new URLSearchParams(r.search));

test.describe("ürün listesi (mock)", () => {
  test("sayfalama: ilk 50 ürün, 'Daha fazla yükle' imleçle devamını getirir", async ({ page }) => {
    const api = await signedIn(page, { catalog: createMockCatalog({ products: 60 }) });
    await page.goto("/urunler");
    const table = page.getByRole("table", { name: "Ürünler" });
    await expect(table.getByRole("row")).toHaveCount(51); // header + 50
    await expect(page.getByText("50 ürün gösteriliyor (devamı var)")).toBeVisible();
    expect(productRequests(api)[0].get("limit")).toBe("50");
    expect(productRequests(api)[0].has("cursor")).toBe(false);

    await page.getByRole("button", { name: "Daha fazla yükle" }).click();
    await expect(table.getByRole("row")).toHaveCount(61);
    await expect(page.getByText("60 ürün gösteriliyor", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Daha fazla yükle" })).toBeHidden();
    expect(productRequests(api).at(-1)!.get("cursor")).toBe("o:50");
  });

  test("arama: gecikmeli, URL'ye yazılır ve sunucuya gönderilir", async ({ page }) => {
    const api = await signedIn(page, { catalog: createMockCatalog({ products: 20 }) });
    await page.goto("/urunler");
    await expect(page.getByRole("table", { name: "Ürünler" }).getByRole("row")).toHaveCount(21);
    await page.getByLabel("Ürün ara").fill("MOCK-007");
    await expect(page).toHaveURL(/\/urunler\?q=MOCK-007$/);
    const rows = page.getByRole("table", { name: "Ürünler" }).getByRole("row");
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(1)).toContainText("Örnek Ürün 007");
    // Debounced: no request per keystroke.
    expect(productRequests(api).filter((p) => p.get("q")).map((p) => p.get("q"))).toEqual(["MOCK-007"]);

    // The URL is the state: reloading keeps the search.
    await page.reload();
    await expect(page.getByLabel("Ürün ara")).toHaveValue("MOCK-007");
    await expect(rows).toHaveCount(2);
  });

  test("filtreler: alt kategoriler dahil kategori, durum ve özel alan (attr.*)", async ({ page }) => {
    const catalog = createMockCatalog({ products: 6 });
    catalog.products[0].status = "inactive";
    const api = await signedIn(page, { catalog });
    await page.goto("/urunler");
    const rows = page.getByRole("table", { name: "Ürünler" }).getByRole("row");
    await expect(rows).toHaveCount(7);

    // "Aydınlatma" has the subcategory "Panel" (even products): the API filters with descendants.
    await page.getByLabel("Kategori").selectOption({ label: "Aydınlatma" });
    await expect(page).toHaveURL(new RegExp(`categoryId=${CATEGORY_IDS.aydinlatma}`));
    await expect(rows).toHaveCount(4);
    expect(productRequests(api).at(-1)!.get("categoryId")).toBe(CATEGORY_IDS.aydinlatma);

    await page.getByLabel("Kategori").selectOption({ label: "Tüm kategoriler" });
    await page.getByLabel("Durum").selectOption({ label: "Yalnızca pasif" });
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(1)).toContainText("Pasif");
    await page.getByLabel("Durum").selectOption({ label: "Aktif ve pasif" });

    await page.getByRole("button", { name: /Özel alan filtreleri/ }).click();
    await page.getByLabel("Güç en az").fill("10");
    await page.getByRole("button", { name: "Filtreleri uygula" }).click();
    await expect(page).toHaveURL(/attr\.guc\.min=10/);
    await expect(rows).toHaveCount(4); // even products have 18 W
    expect(productRequests(api).at(-1)!.get("attr.guc.min")).toBe("10");

    await page.getByLabel("IP sınıfı").selectOption("IP65");
    await page.getByRole("button", { name: "Filtreleri uygula" }).click();
    await expect(page.getByRole("heading", { name: "Eşleşen ürün bulunamadı" })).toBeVisible();
    await page.getByRole("button", { name: "Aramayı ve filtreleri temizle" }).first().click();
    await expect(page).toHaveURL(/\/urunler$/);
    await expect(rows).toHaveCount(7);
  });

  test("boş ve hata durumları", async ({ page }) => {
    const catalog = createMockCatalog({ products: 0 });
    await signedIn(page, { catalog });
    catalog.overrides.set("GET /api/v1/products", (route) => fulfillJson(route, 500, problemBody(500, "server.error")));
    await page.goto("/urunler");
    await expect(page.getByRole("heading", { name: "Ürünler yüklenemedi" })).toBeVisible();
    await page.getByRole("button", { name: "Tekrar dene" }).click();
    await expect(page.getByRole("heading", { name: "Henüz ürün yok" })).toBeVisible();
    await expect(page.getByRole("link", { name: "İlk ürünü ekle" })).toBeVisible();
  });

  test("mobil: kart görünümü, yatay kaydırma yok", async ({ page }) => {
    await page.setViewportSize(MOBILE);
    await signedIn(page, { catalog: createMockCatalog({ products: 5 }) });
    await page.goto("/urunler");
    const cards = page.getByRole("list", { name: "Ürünler" }).getByRole("listitem");
    await expect(cards).toHaveCount(5);
    await expect(page.getByRole("table", { name: "Ürünler" })).toBeHidden();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    await expectNoSeriousViolations(page);
  });

  test("sık görünüm tercihi hatırlanır", async ({ page }) => {
    await signedIn(page, { catalog: createMockCatalog({ products: 2 }) });
    await page.goto("/urunler");
    const toggle = page.getByRole("button", { name: "Sık görünüm" });
    await expect(toggle).toHaveAttribute("aria-pressed", "false");
    await toggle.click();
    await expect(toggle).toHaveAttribute("aria-pressed", "true");
    await page.reload();
    await expect(page.getByRole("button", { name: "Sık görünüm" })).toHaveAttribute("aria-pressed", "true");
  });
});

test.describe("ürün oluştur / düzenle (mock)", () => {
  test("özel alanlarla yeni ürün: tipli değerler ve Idempotency-Key gönderilir", async ({ page }) => {
    const catalog = createMockCatalog({ products: 0 });
    const api = await signedIn(page, { catalog });
    await page.goto("/urunler");
    await page.getByRole("link", { name: "Yeni ürün" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Yeni ürün" })).toBeVisible();
    // The previous route stays mounted but hidden (Next.js keeps it in an <Activity>), so scope to the form.
    const form = page.getByRole("form", { name: "Yeni ürün" });

    await form.getByLabel("Ürün adı").fill("LED Panel 60x60 (mock)");
    await form.getByLabel("Kategori").selectOption({ label: "— Panel" });
    await form.getByLabel("Marka").selectOption({ label: "Parlak (mock)" });
    await form.getByLabel("KDV oranı").fill("10");
    await form.getByLabel("Güç").fill("36,5");
    await form.getByLabel("Renk sıcaklığı en az").fill("3000");
    await form.getByLabel("Renk sıcaklığı en çok").fill("6500");
    await form.getByLabel("IP sınıfı").selectOption("IP44");
    await form.getByRole("group", { name: "Uyumlu model" }).getByLabel("CB150").check();
    await form.getByLabel("Dış mekân").selectOption({ label: "Evet" });
    await form.getByLabel("Garanti (ay)").fill("24");
    await form.getByLabel("Üretim tarihi").fill("2026-10-01");
    await form.getByLabel("SKU (stok kodu)").fill("PNL-6060");
    await form.getByLabel("Barkod").fill("2000000999991");
    await form.getByLabel("Renk", { exact: true }).selectOption("Beyaz");
    await page.getByRole("button", { name: "Ürünü oluştur" }).click();

    await expect(page.getByRole("heading", { level: 1, name: "LED Panel 60x60 (mock)" })).toBeVisible();
    await expect(page).toHaveURL(/\/urunler\/[0-9a-f-]{36}$/);
    const post = api.requests.find((r) => r.method === "POST" && r.path === "/api/v1/products")!;
    expect(post.headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(post.headers["x-tenant-id"]).toBe(TENANT_A.tenantId);
    expect(post.body).toMatchObject({
      name: "LED Panel 60x60 (mock)",
      categoryId: CATEGORY_IDS.panel,
      taxRate: 10,
      attributes: {
        guc: 36.5,
        renk_sicakligi: { min: 3000, max: 6500 },
        ip_sinifi: "IP44",
        uyumlu_model: ["CB150"],
        dis_mekan: true,
        garanti_ay: 24,
        uretim_tarihi: "2026-10-01",
      },
      variants: [{ sku: "PNL-6060", barcode: "2000000999991", attributes: { renk: "Beyaz" } }],
    });
    // Saved values come back into the form.
    await expect(page.getByLabel("Güç")).toHaveValue("36,5");
    await expect(page.getByRole("table", { name: "Varyantlar" })).toContainText("PNL-6060");
  });

  test("istemci doğrulaması: zorunlu alanlar işaretlenir, istek gönderilmez", async ({ page }) => {
    const api = await signedIn(page, { catalog: createMockCatalog({ products: 0 }) });
    await page.goto("/urunler/yeni");
    await page.getByRole("button", { name: "Ürünü oluştur" }).click();
    await expect(page.getByLabel("Ürün adı")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByLabel("Ürün adı")).toBeFocused();
    await expect(page.getByText("Ürün adı zorunlu.")).toBeVisible();
    await expect(page.getByLabel("Güç")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByLabel("SKU (stok kodu)")).toHaveAttribute("aria-invalid", "true");
    expect(api.requests.some((r) => r.method === "POST")).toBe(false);
  });

  test("422: alan hataları doğru girdilere yazılır; aynı deneme aynı Idempotency-Key ile tekrarlanır", async ({ page }) => {
    const catalog = createMockCatalog({ products: 0 });
    const api = await signedIn(page, { catalog });
    catalog.overrides.set("POST /api/v1/products", (route) =>
      fulfillJson(
        route,
        422,
        problemBody(422, "validation.failed", {
          errors: {
            "attributes.guc": ["Must be a number."],
            "variants[0].attributes.renk": ["Must be one of: Sarı, Beyaz."],
            "variants[0].unitId": ["Unit not found."],
          },
        }),
      ),
    );
    await page.goto("/urunler/yeni");
    await page.getByLabel("Ürün adı").fill("Sunucu hatası denemesi (mock)");
    await page.getByLabel("Güç").fill("10");
    await page.getByLabel("SKU (stok kodu)").fill("ERR-1");
    await page.getByRole("button", { name: "Ürünü oluştur" }).click();

    await expect(page.getByLabel("Güç")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByText("Geçerli bir sayı girin.")).toBeVisible();
    await expect(page.getByLabel("Renk", { exact: true })).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByText("Şunlardan biri olmalı: Sarı, Beyaz.")).toBeVisible();
    // Errors without an input on screen go to the summary.
    await expect(page.getByRole("alert").filter({ hasText: "Kaydedilemedi" })).toContainText("Birim bulunamadı veya arşivlenmiş.");
    await expect(page.getByLabel("Güç")).toBeFocused();

    // Retry of the same body: same key (no duplicate product on the server).
    await page.getByRole("button", { name: "Ürünü oluştur" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Sunucu hatası denemesi (mock)" })).toBeVisible();
    const posts = api.requests.filter((r) => r.method === "POST" && r.path === "/api/v1/products");
    expect(posts).toHaveLength(2);
    expect(posts[1].headers["idempotency-key"]).toBe(posts[0].headers["idempotency-key"]);
  });

  test("412: başkası değiştirdiyse uyarı, yeniden yükle ve tekrar kaydet", async ({ page }) => {
    const catalog = createMockCatalog({ products: [makeProduct(1, { name: "Fren Balatası (mock)" })] });
    const api = await signedIn(page, { catalog });
    await page.goto(`/urunler/${productId(1)}`);
    await expect(page.getByLabel("Ürün adı")).toHaveValue("Fren Balatası (mock)");

    // Someone else saves the product meanwhile.
    Object.assign(catalog.products[0], { name: "Fren Balatası Ön (mock)", version: catalog.products[0].version + 1 });

    await page.getByLabel("Ürün adı").fill("Fren Balatası Arka (mock)");
    await page.getByRole("button", { name: "Değişiklikleri kaydet" }).click();
    const alert = page.getByRole("alert").filter({ hasText: "Kayıt başkası tarafından değiştirildi" });
    await expect(alert).toBeVisible();
    const patch = api.requests.find((r) => r.method === "PATCH")!;
    expect(patch.headers["if-match"]).toBe('W/"1"');
    expect(patch.headers["content-type"]).toContain("application/merge-patch+json");
    expect(patch.body).toEqual({ name: "Fren Balatası Arka (mock)" });

    await alert.getByRole("button", { name: "Yeniden yükle" }).click();
    await expect(page.getByLabel("Ürün adı")).toHaveValue("Fren Balatası Ön (mock)");
    await expect(alert).toBeHidden();
    await page.getByLabel("Ürün adı").fill("Fren Balatası Arka (mock)");
    await page.getByRole("button", { name: "Değişiklikleri kaydet" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Fren Balatası Arka (mock)" })).toBeVisible();
    expect(api.requests.filter((r) => r.method === "PATCH").at(-1)!.headers["if-match"]).toBe('W/"2"');
  });

  test("arşivleme onay diyaloğuyla yapılır", async ({ page }) => {
    const catalog = createMockCatalog({ products: [makeProduct(1, { name: "Arşivlenecek (mock)" })] });
    const api = await signedIn(page, { catalog });
    await page.goto(`/urunler/${productId(1)}`);
    await page.getByRole("button", { name: "Arşivle", exact: true }).click();
    const dialog = page.getByRole("alertdialog", { name: "Ürünü arşivle" });
    await expect(dialog).toBeVisible();
    await expectNoSeriousViolations(page);
    await dialog.getByRole("button", { name: "Vazgeç" }).click();
    await expect(dialog).toBeHidden();
    expect(api.requests.some((r) => r.method === "DELETE")).toBe(false);

    await page.getByRole("button", { name: "Arşivle", exact: true }).click();
    await dialog.getByRole("button", { name: "Arşivle" }).click();
    await expect(page).toHaveURL(/\/urunler$/);
    expect(catalog.products[0].status).toBe("archived");
    expect(api.requests.find((r) => r.method === "DELETE")!.headers["if-match"]).toBe('W/"1"');
  });

  test("varyant ekleme ve düzenleme kendi sürümüyle yapılır", async ({ page }) => {
    const catalog = createMockCatalog({ products: [makeProduct(1)] });
    const api = await signedIn(page, { catalog });
    await page.goto(`/urunler/${productId(1)}`);
    await page.getByRole("button", { name: "Varyant ekle" }).click();
    const dialog = page.getByRole("dialog", { name: "Varyant ekle" });
    await dialog.getByLabel("SKU (stok kodu)").fill("MOCK-001-2");
    await dialog.getByLabel("Renk", { exact: true }).selectOption("Sarı");
    await dialog.getByRole("button", { name: "Varyantı ekle" }).click();
    await expect(page.getByRole("table", { name: "Varyantlar" }).getByRole("row")).toHaveCount(3);
    const post = api.requests.find((r) => r.method === "POST" && r.path.endsWith("/variants"))!;
    expect(post.headers["idempotency-key"]).toBeTruthy();
    expect(post.body).toMatchObject({ sku: "MOCK-001-2", attributes: { renk: "Sarı" } });

    await page.getByRole("button", { name: "MOCK-001-1 varyantını düzenle" }).click();
    const edit = page.getByRole("dialog", { name: /Varyantı düzenle/ });
    await edit.getByLabel("Barkod").fill("");
    await edit.getByRole("button", { name: "Kaydet" }).click();
    await expect(edit).toBeHidden();
    const patch = api.requests.find((r) => r.method === "PATCH" && r.path.startsWith("/api/v1/variants/"))!;
    expect(patch.body).toEqual({ barcode: null });
    expect(patch.headers["if-match"]).toBe('W/"1"');
  });

  test("409 barkod çakışması: hata çakışan varyantın SKU'suyla ilgili alana yazılır", async ({ page }) => {
    const catalog = createMockCatalog({ products: [makeProduct(1)] });
    catalog.overrides.set(`POST /api/v1/products/${productId(1)}/variants`, (route) =>
      fulfillJson(
        route,
        409,
        problemBody(409, "catalog.barcode_taken", {
          conflict: { field: "barcode", value: "8690000099999", variantId: "00000000-0000-4000-8000-00000000ff01", productId: "00000000-0000-4000-8000-00000000ff02", sku: "LED-9" },
        }),
      ),
    );
    await signedIn(page, { catalog });
    await page.goto(`/urunler/${productId(1)}`);
    await page.getByRole("button", { name: "Varyant ekle" }).click();
    const dialog = page.getByRole("dialog", { name: "Varyant ekle" });
    await dialog.getByLabel("SKU (stok kodu)").fill("MOCK-001-2");
    await dialog.getByLabel("Barkod").fill("8690000099999");
    await dialog.getByLabel("Renk", { exact: true }).selectOption("Sarı");
    await dialog.getByRole("button", { name: "Varyantı ekle" }).click();
    await expect(dialog.getByLabel("Barkod")).toHaveAttribute("aria-invalid", "true");
    await expect(dialog.getByText("Bu barkod başka bir üründeki varyantta (SKU LED-9) kullanılıyor. Farklı bir barkod girin.")).toBeVisible();
  });

  test("ürün sayfası erişilebilirlik (axe) — masaüstü ve mobil", async ({ page }) => {
    await signedIn(page, { catalog: createMockCatalog({ products: [makeProduct(1, { name: "Erişilebilir (mock)" })] }) });
    await page.goto(`/urunler/${productId(1)}`);
    await expect(page.getByLabel("Ürün adı")).toHaveValue("Erişilebilir (mock)");
    await expectNoSeriousViolations(page);
    await page.setViewportSize(MOBILE);
    await expectNoSeriousViolations(page);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
});

test.describe("yetkiler (mock)", () => {
  test("satış rolü: salt okunur liste ve ürün sayfası", async ({ page }) => {
    const api = await signedIn(page, { memberships: [SALES], catalog: createMockCatalog({ products: 2 }) });
    await page.goto("/urunler");
    await expect(page.getByRole("table", { name: "Ürünler" }).getByRole("row")).toHaveCount(3);
    await expect(page.getByRole("link", { name: "Yeni ürün" })).toHaveCount(0);

    await page.getByRole("link", { name: "Örnek Ürün 001" }).click();
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByLabel("Ürün adı")).toBeDisabled();
    await expect(page.getByRole("button", { name: "Değişiklikleri kaydet" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Arşivle", exact: true })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Varyant ekle" })).toHaveCount(0);
    await expectNoSeriousViolations(page);

    await page.goto("/urunler/yeni");
    await expect(page.getByRole("heading", { name: "Ürün ekleme yetkiniz yok" })).toBeVisible();
    expect(api.requests.some((r) => r.method !== "GET")).toBe(false);
  });
});

test.describe("ürün sayfası yönlendirme (#87, mock)", () => {
  test("/urunler/[id] doğrudan açılır; eski /urunler/duzenle?id= adresi yönlendirilir", async ({ page }) => {
    await signedIn(page, { catalog: createMockCatalog({ products: [makeProduct(1, { name: "Dinamik Rota (mock)" })] }) });
    await page.goto(`/urunler/${productId(1)}`);
    await expect(page.getByRole("heading", { level: 1, name: "Dinamik Rota (mock)" })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Ana menü" }).getByRole("link", { name: "Ürünler" })).toHaveAttribute(
      "aria-current",
      "page",
    );
    await expect(page).toHaveTitle("Ürün · BayiOS");

    await page.goto(`/urunler/duzenle?id=${productId(1)}`);
    await expect(page).toHaveURL(new RegExp(`/urunler/${productId(1)}$`));
    await expect(page.getByRole("heading", { level: 1, name: "Dinamik Rota (mock)" })).toBeVisible();

    await page.goto("/urunler/duzenle");
    await expect(page).toHaveURL(/\/urunler$/);
  });

  test("bilinmeyen ürün kimliği: bulunamadı durumu", async ({ page }) => {
    await signedIn(page, { catalog: createMockCatalog({ products: 1 }) });
    await page.goto("/urunler/00000000-0000-4000-8000-d99999999999");
    await expect(page.getByRole("heading", { name: /bulunamadı/i })).toBeVisible();
  });
});

test.describe("ayarlar → özel alanlar (mock)", () => {
  test("sahip yeni alan ekler; anahtar etiketten önerilir", async ({ page }) => {
    const catalog = createMockCatalog({ products: 0 });
    const api = await signedIn(page, { catalog });
    await page.goto("/ayarlar");
    await page.getByRole("link", { name: /Özel alanlar/ }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Özel alanlar" })).toBeVisible();
    await expect(page.getByRole("table", { name: "Özel alanlar" })).toContainText("Güç");
    await expectNoSeriousViolations(page);

    await page.getByRole("button", { name: "Yeni alan" }).click();
    const dialog = page.getByRole("dialog", { name: "Yeni özel alan" });
    await dialog.getByLabel("Alan adı").fill("Işık açısı");
    await expect(dialog.getByLabel("Anahtar")).toHaveValue("isik_acisi");
    await dialog.getByLabel("Veri türü").selectOption({ label: "Tam sayı" });
    await dialog.getByLabel("Birim").fill("°");
    await dialog.getByLabel("Ürün listesinde filtre olarak kullanılsın").check();
    await expectNoSeriousViolations(page);
    await dialog.getByRole("button", { name: "Alanı ekle" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("table", { name: "Özel alanlar" })).toContainText("Işık açısı");
    const post = api.requests.find((r) => r.method === "POST" && r.path === "/api/v1/custom-fields")!;
    expect(post.headers["idempotency-key"]).toBeTruthy();
    expect(post.body).toMatchObject({ entityType: "product", key: "isik_acisi", dataType: "integer", unit: "°", filterable: true, required: false });
  });

  test("müdür rolü özel alanları yalnızca görür", async ({ page }) => {
    await signedIn(page, { memberships: [MANAGER], catalog: createMockCatalog({ products: 0 }) });
    await page.goto("/ayarlar/ozel-alanlar");
    await expect(page.getByText("Salt okunur görünüm")).toBeVisible();
    await expect(page.getByRole("button", { name: "Yeni alan" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /alanını düzenle/ })).toHaveCount(0);
  });
});

test.describe("erişilebilirlik — yeni ürün formu", () => {
  for (const [name, viewport] of [
    ["desktop", DESKTOP],
    ["mobile", MOBILE],
  ] as const) {
    test(`/urunler/yeni (${name})`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await signedIn(page, { catalog: createMockCatalog({ products: 0 }) });
      await page.goto("/urunler/yeni");
      await expect(page.getByLabel("Ürün adı")).toBeVisible();
      await expectNoSeriousViolations(page);
    });
  }
});
