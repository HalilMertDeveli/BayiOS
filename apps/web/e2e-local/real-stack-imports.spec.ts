import { readFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { buildXlsx } from "./demo/xlsx.mjs";
import { logApiErrors, newAccount, signUpAndOnboard } from "./support/real-stack";

/**
 * REAL END-TO-END (#94 web) — no mocks: Supabase Auth + BayiOS API (Imports background jobs) + PostgreSQL 17 +
 * production web build. CI: .github/workflows/e2e-real.yml (`npm run test:e2e:real`, file name matches "real-stack").
 *
 * A new business uploads a small generated .xlsx through the import wizard, keeps the API's column suggestions, sees
 * the dry-run preview (one invalid row, downloadable as CSV), imports the valid rows, finds them in /urunler, and rolls
 * the import back (products archived, not deleted). All names and SKUs are synthetic and unique per run.
 */
const stamp = `${Date.now()}`.slice(-9);
const P1 = { sku: `IMP-${stamp}-1`, name: `E2E İçe Aktarma Balata ${stamp}` };
const P2 = { sku: `IMP-${stamp}-2`, name: `E2E İçe Aktarma Zincir ${stamp}` };
const BAD = { sku: `IMP-${stamp}-3` };
const FILE = `e2e-urunler-${stamp}.xlsx`;

test("içe aktarma sihirbazı: xlsx yükle → eşle → önizle → aktar → ürünlerde gör → geri al (gerçek API)", async ({ page }) => {
  test.setTimeout(180_000);
  logApiErrors(page);
  await signUpAndOnboard(page, newAccount("imp"));

  await page.goto("/ice-aktarma");
  await expect(page.getByRole("heading", { level: 1, name: "İçe aktarma" })).toBeVisible();
  await expect(page.getByText("Henüz içe aktarma yapılmadı").first()).toBeVisible();
  await page.getByRole("link", { name: "Yeni içe aktarma" }).first().click();

  // Steps 1–2: products, the generated workbook (motorcycle template: unit "adet" exists, VAT 20).
  await page.getByRole("radio", { name: /Ürünler/ }).check();
  await page.getByRole("button", { name: "Devam" }).click();
  const xlsx = buildXlsx("Urunler", [
    ["Stok Kodu", "Ürün Adı", "Birim", "KDV", "Açıklama"],
    [P1.sku, P1.name, "adet", 20, "E2E sentetik ürün"],
    [P2.sku, P2.name, "adet", 20, ""],
    [BAD.sku, "", "adet", 20, "adı eksik → hatalı satır"],
  ]);
  await page.locator("input[type=file]").setInputFiles({
    name: FILE,
    mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    buffer: Buffer.from(xlsx),
  });
  const uploaded = page.waitForResponse((r) => r.url().endsWith("/api/v1/import-jobs") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Yükle ve devam et" }).click();
  expect((await uploaded).status()).toBe(201);

  // Step 3: the API's suggestions are prefilled (and marked), nothing saved yet.
  await expect(page.getByRole("heading", { level: 2, name: "Kolonları eşleyin" })).toBeVisible();
  const column = (header: string) => page.getByRole("combobox", { name: `“${header}” kolonu hangi alana aktarılsın?` });
  await expect(column("Stok Kodu")).toHaveValue("sku");
  await expect(column("Ürün Adı")).toHaveValue("name");
  await expect(column("Birim")).toHaveValue("unit");
  await expect(column("KDV")).toHaveValue("taxRate");
  await expect(column("Açıklama")).toHaveValue("description");
  await expect(page.getByText(/^Öneri %\d+$/).first()).toBeVisible();

  // Step 4: dry run in the background, polled until validated.
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Önizleme", exact: true })).toBeVisible({ timeout: 60_000 });
  const summary = page.getByLabel("Önizleme özeti");
  await expect(summary).toContainText("Eklenecek2");
  await expect(summary).toContainText("Hatalı1");
  await expect(page.getByRole("table", { name: "Önizleme satırları" })).toContainText(BAD.sku);

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Hata raporunu indir (CSV)" }).click();
  const report = await download;
  expect(report.suggestedFilename()).toMatch(/\.csv$/);
  const csv = await readFile(await report.path(), "utf8");
  expect(csv).toContain(BAD.sku);
  await page.screenshot({ path: "test-results/real-stack-import-preview.png", fullPage: true });

  // Step 5–6: run, poll the progress, result.
  await page.getByRole("button", { name: "2 satırı aktar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma tamamlandı" })).toBeVisible({ timeout: 60_000 });
  const result = page.getByLabel("Aktarma sonucu");
  await expect(result).toContainText("Eklendi2");
  await expect(page.getByRole("button", { name: "Geri al", exact: true })).toBeVisible();
  await page.screenshot({ path: "test-results/real-stack-import-result.png", fullPage: true });

  // The products are in the catalog.
  await page.getByRole("link", { name: "Ürünlere git" }).click();
  await expect(page).toHaveURL(/\/urunler$/);
  await expect(page.getByText(P1.name).first()).toBeVisible();
  await expect(page.getByText(P2.name).first()).toBeVisible();

  // History lists the import; roll it back (archive, not delete).
  await page.goto("/ice-aktarma");
  const history = page.getByRole("table", { name: "İçe aktarma geçmişi" });
  await expect(history).toContainText("Tamamlandı");
  await history.getByRole("link", { name: FILE }).click();
  await page.getByRole("button", { name: "Geri al", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Aktarmayı geri al" });
  await expect(dialog).toContainText("2 ürün silinmez, arşivlenir");
  const rollback = page.waitForResponse((r) => r.url().endsWith("/rollback") && r.request().method() === "POST");
  await dialog.getByRole("button", { name: "Geri al" }).click();
  expect((await rollback).status()).toBe(202);
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma geri alındı" })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByLabel("Geri alma sonucu")).toContainText("Geri alındı2");

  await page.goto("/urunler");
  await expect(page.getByRole("heading", { level: 1, name: "Ürünler" })).toBeVisible();
  // The only products of this new business were archived: the default (non-archived) list is empty.
  await expect(page.getByText("Henüz ürün yok").first()).toBeVisible();
  await expect(page.getByText(P1.name)).toHaveCount(0);
});
