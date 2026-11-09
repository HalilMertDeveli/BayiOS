import { expect, test } from "@playwright/test";
import { apiOk, captureApiSession, logApiErrors, newAccount, signUpAndOnboard } from "./support/real-stack";

/**
 * REAL END-TO-END (#129 web) — no mocks: Supabase Auth + BayiOS API + PostgreSQL 17 + production web build.
 * CI: .github/workflows/e2e-real.yml (`npm run test:e2e:real`, file name matches "real-stack").
 *
 * A new business right after onboarding: the dashboard loads its tiles from the real endpoints (no tile errors), then
 * the owner manages brands, the category tree and units on the new screens and creates a product with a brand added
 * inline from the product form. All names are synthetic and unique per run.
 */
const stamp = `${Date.now()}`.slice(-9);
const BRAND = `E2E Marka ${stamp}`;
const ROOT = `E2E Kategori ${stamp}`;
const CHILD = `Alt ${stamp}`;
const UNIT = { code: `e2e${stamp}`, name: `E2E Kilo ${stamp}` };
const PRODUCT = { name: `E2E Katalog Ürünü ${stamp}`, sku: `CAT-${stamp}`, brand: `Formdan ${stamp}` };

test("pano + marka/kategori/birim ekranları + üründe satır içi yeni marka (gerçek API)", async ({ page }) => {
  test.setTimeout(180_000);
  logApiErrors(page);
  await signUpAndOnboard(page, newAccount("cat"));

  // ---- Pano: every tile of the owner loads from the real API (empty business: empty states, no errors).
  await expect(page.getByRole("heading", { level: 1, name: "Pano" })).toBeVisible();
  for (const name of ["Onay bekleyen siparişler", "Bugünkü siparişler", "Kritik stok", "Görevlerim", "Paket"]) {
    await expect(page.getByRole("region", { name })).toBeVisible();
  }
  await expect(page.getByRole("region", { name: "Onay bekleyen siparişler" }).getByText("Onay bekleyen sipariş yok.")).toBeVisible();
  await expect(page.getByRole("region", { name: "Bugünkü siparişler" }).getByText("Bugün henüz sipariş oluşturulmadı.")).toBeVisible();
  await expect(page.getByRole("region", { name: "Görevlerim" }).getByText("Size atanmış veya havuzda bekleyen görev yok.")).toBeVisible();
  await expect(page.getByRole("region", { name: "Paket" })).toContainText("Ücretsiz deneme");
  await expect(page.getByRole("main").getByRole("alert")).toHaveCount(0);
  await page.screenshot({ path: "test-results/real-stack-pano-tiles.png", fullPage: true });

  // ---- Brands.
  await page.goto("/urunler/markalar");
  await page.getByRole("button", { name: "Yeni marka" }).first().click();
  await page.getByRole("dialog", { name: "Yeni marka" }).getByLabel("Marka adı").fill(BRAND);
  const brandCreated = page.waitForResponse((r) => r.url().endsWith("/api/v1/brands") && r.request().method() === "POST");
  await page.getByRole("dialog", { name: "Yeni marka" }).getByRole("button", { name: "Markayı ekle" }).click();
  expect((await brandCreated).status()).toBe(201);
  const brands = page.getByRole("table", { name: "Markalar" });
  await expect(brands.getByRole("row", { name: new RegExp(BRAND) })).toBeVisible();
  // Same name again: the API's 409 is shown on the field.
  await page.getByRole("button", { name: "Yeni marka" }).first().click();
  await page.getByRole("dialog", { name: "Yeni marka" }).getByLabel("Marka adı").fill(BRAND.toUpperCase());
  await page.getByRole("dialog", { name: "Yeni marka" }).getByRole("button", { name: "Markayı ekle" }).click();
  await expect(page.getByRole("dialog", { name: "Yeni marka" }).getByText("Bu marka adı zaten kayıtlı.")).toBeVisible();
  await page.getByRole("dialog", { name: "Yeni marka" }).getByRole("button", { name: "Vazgeç" }).click();
  // Rename (If-Match).
  await page.getByRole("button", { name: `${BRAND} markasını yeniden adlandır` }).click();
  await page.getByRole("dialog", { name: /Markayı yeniden adlandır/ }).getByLabel("Marka adı").fill(`${BRAND} Yeni`);
  await page.getByRole("dialog", { name: /Markayı yeniden adlandır/ }).getByRole("button", { name: "Kaydet" }).click();
  await expect(brands.getByRole("row", { name: new RegExp(`${BRAND} Yeni`) })).toBeVisible();

  // ---- Categories: a root, a child under it; the root cannot be archived while the child is active.
  await page.getByRole("navigation", { name: "Katalog tanımları" }).getByRole("link", { name: "Kategoriler" }).click();
  await page.getByRole("button", { name: "Yeni ana kategori" }).first().click();
  await page.getByRole("dialog", { name: "Yeni ana kategori" }).getByLabel("Kategori adı").fill(ROOT);
  await page.getByRole("dialog", { name: "Yeni ana kategori" }).getByRole("button", { name: "Kategoriyi ekle" }).click();
  await page.getByRole("button", { name: `${ROOT}: alt kategori ekle` }).click();
  await page.getByRole("dialog", { name: `${ROOT} altına kategori` }).getByLabel("Kategori adı").fill(CHILD);
  await page.getByRole("dialog", { name: `${ROOT} altına kategori` }).getByRole("button", { name: "Kategoriyi ekle" }).click();
  const tree = page.getByRole("table", { name: "Kategori ağacı" });
  await expect(tree.getByRole("row", { name: new RegExp(CHILD) })).toContainText("seviye 2");
  await page.getByRole("button", { name: `${ROOT} kategorisini arşivle` }).click();
  await expect(page.getByRole("alertdialog", { name: "Kategoriyi arşivle" })).toContainText("aktif alt kategorisi var");
  await page.getByRole("alertdialog", { name: "Kategoriyi arşivle" }).getByRole("button", { name: "Tamam" }).click();
  // Archive the child (DELETE + If-Match) — then it is gone from the active tree.
  const archived = page.waitForResponse((r) => r.url().includes("/api/v1/categories/") && r.request().method() === "DELETE");
  await page.getByRole("button", { name: `${CHILD} kategorisini arşivle` }).click();
  await page.getByRole("alertdialog", { name: "Kategoriyi arşivle" }).getByRole("button", { name: "Arşivle" }).click();
  expect((await archived).status()).toBe(204);
  await expect(tree.getByRole("row", { name: new RegExp(CHILD) })).toHaveCount(0);

  // ---- Units: decimal quantities allowed; the API stores the code lower case.
  await page.getByRole("navigation", { name: "Katalog tanımları" }).getByRole("link", { name: "Birimler" }).click();
  await page.getByRole("button", { name: "Yeni birim" }).first().click();
  const unitDialog = page.getByRole("dialog", { name: "Yeni birim" });
  await unitDialog.getByLabel("Kod").fill(UNIT.code.toUpperCase());
  await unitDialog.getByLabel("Birim adı").fill(UNIT.name);
  await unitDialog.getByLabel("Küsuratlı miktar kabul eder").check();
  await unitDialog.getByRole("button", { name: "Birimi ekle" }).click();
  const unitRow = page.getByRole("table", { name: "Birimler" }).getByRole("row", { name: new RegExp(UNIT.name) });
  await expect(unitRow).toContainText(UNIT.code);
  await expect(unitRow).toContainText("Evet");

  // ---- Product form: "+ Yeni marka" creates the brand and selects it; the product is saved with it.
  const session = await captureApiSession(page, () => page.goto("/urunler/yeni"));
  const form = page.getByRole("form", { name: "Yeni ürün" });
  await form.getByLabel("Ürün adı").fill(PRODUCT.name);
  await form.getByRole("button", { name: "Yeni marka" }).click();
  await page.getByRole("dialog", { name: "Yeni marka" }).getByLabel("Marka adı").fill(PRODUCT.brand);
  await page.getByRole("dialog", { name: "Yeni marka" }).getByRole("button", { name: "Markayı ekle" }).click();
  await expect(form.getByLabel("Marka", { exact: true }).locator("option:checked")).toHaveText(PRODUCT.brand);
  await form.getByLabel("Kategori", { exact: true }).selectOption({ label: ROOT });
  await form.getByLabel("SKU (stok kodu)").fill(PRODUCT.sku);
  const created = page.waitForResponse((r) => r.url().endsWith("/api/v1/products") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Ürünü oluştur" }).click();
  expect((await created).status()).toBe(201);

  const products = await apiOk<{ items: { name: string; brandId: string | null; categoryId: string | null }[] }>(
    session,
    "GET",
    `/api/v1/products?q=${encodeURIComponent(PRODUCT.sku)}`,
  );
  const allBrands = await apiOk<{ items: { id: string; name: string }[] }>(session, "GET", "/api/v1/brands");
  const brandId = allBrands.items.find((b) => b.name === PRODUCT.brand)?.id;
  expect(brandId).toBeTruthy();
  expect(products.items.map((p) => [p.name, p.brandId])).toEqual([[PRODUCT.name, brandId]]);
  await page.screenshot({ path: "test-results/real-stack-catalog-product.png", fullPage: true });
});
