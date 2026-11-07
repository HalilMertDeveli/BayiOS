/**
 * İçe aktarma (#94 web): history, wizard (type → file → mapping → preview → progress → result), templates, CSV error
 * report, cancel, rollback with conflict rows, 429 upload limit, file rejections, permission locks, mobile, axe.
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts, support/mock-imports.ts);
 * every file, SKU and name is synthetic. Nothing here talks to a real API (the real-stack run is
 * e2e-local/real-stack-imports.spec.ts).
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, USER, signedIn, type MockMembership } from "./support/mock-backend";
import { DEFAULT_PRODUCT_FILE, createMockImports, importedRows, mockJob, type MockImports } from "./support/mock-imports";
import { PRICE_LIST_IDS } from "./support/mock-commerce";

test.use({ viewport: DESKTOP });

const XLSX_MIME = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
const MANAGER: MockMembership = { ...TENANT_A, role: "manager" };
const SALES: MockMembership = { ...TENANT_A, role: "sales" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const DEFAULT_MAPPING = { sku: "Stok Kodu", name: "Ürün Adı", unit: "Birim", taxRate: "KDV %" };

/** Steps 1–2 on /ice-aktarma/yeni: products, pick a (synthetic) file, upload. */
async function uploadProducts(page: Page, fileName = "urunler.xlsx") {
  await page.goto("/ice-aktarma/yeni");
  await expect(page.getByRole("heading", { level: 2, name: "Ne aktaracaksınız?" })).toBeVisible();
  await page.getByRole("radio", { name: /Ürünler/ }).check();
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Dosyayı yükleyin" })).toBeVisible();
  await page.locator("input[type=file]").setInputFiles({ name: fileName, mimeType: XLSX_MIME, buffer: Buffer.from("PK mock xlsx bytes") });
  await page.getByRole("button", { name: "Yükle ve devam et" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Kolonları eşleyin" })).toBeVisible();
}

const columnSelect = (page: Page, header: string) => page.getByRole("combobox", { name: `“${header}” kolonu hangi alana aktarılsın?` });
const mappingPut = (imports: MockImports) => imports.requests.filter((r) => r.method === "PUT" && r.path.endsWith("/mapping"));

test("happy path: upload → suggested mapping → preview → import → result and history (MOCK)", async ({ page }) => {
  const api = await signedIn(page);
  const imports = api.imports!;
  await page.goto("/ice-aktarma");
  await expect(page.getByRole("heading", { level: 1, name: "İçe aktarma" })).toBeVisible();
  await expect(page.getByText("Henüz içe aktarma yapılmadı").first()).toBeVisible();
  await expectNoSeriousViolations(page);
  await page.getByRole("link", { name: "Yeni içe aktarma" }).first().click();
  await expect(page).toHaveURL(/\/ice-aktarma\/yeni$/);
  await expect(page.getByRole("list", { name: "İçe aktarma adımları" }).getByRole("listitem").nth(0)).toHaveAttribute("aria-current", "step");
  await expectNoSeriousViolations(page);

  await uploadProducts(page);
  const upload = imports.requests.find((r) => r.method === "POST" && r.path === "/api/v1/import-jobs")!;
  expect(upload.headers["idempotency-key"]).toBeTruthy();
  expect(upload.headers["content-type"]).toMatch(/^multipart\/form-data; boundary=/);

  // Suggestions are prefilled and marked — never saved without the user.
  await expect(page.getByRole("status").filter({ hasText: "Öneriler ön seçili" })).toBeVisible();
  await expect(columnSelect(page, "Stok Kodu")).toHaveValue("sku");
  await expect(columnSelect(page, "Ürün Adı")).toHaveValue("name");
  await expect(columnSelect(page, "Renk")).toHaveValue("attr.renk");
  await expect(columnSelect(page, "Not")).toHaveValue("");
  await expect(page.getByText("Öneri %95").first()).toBeVisible();
  expect(mappingPut(imports)).toHaveLength(0);
  // The price column is suggested too; without a list the products import keeps it out by default here.
  await columnSelect(page, "Satış Fiyatı").selectOption("");
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Önizleme", exact: true })).toBeVisible();
  const put = mappingPut(imports);
  expect(put).toHaveLength(1);
  await expect(page.getByLabel("Önizleme özeti")).toContainText("Eklenecek2");
  await expect(page.getByLabel("Önizleme özeti")).toContainText("Güncellenecek1");
  await expect(page.getByLabel("Önizleme özeti")).toContainText("Hatalı2");
  // Rows default to the error filter when there are errors.
  await expect(page.getByRole("button", { name: /Hatalı/, pressed: true })).toBeVisible();
  await expect(page.getByRole("table", { name: "Önizleme satırları" }).getByText("Ürün adı zorunlu.")).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "3 satırı aktar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarılıyor" })).toBeVisible();
  await expect(page.getByRole("progressbar", { name: "Aktarma ilerlemesi" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma tamamlandı" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByLabel("Aktarma sonucu")).toContainText("Eklendi2");
  await expect(page.getByLabel("Aktarma sonucu")).toContainText("Güncellendi1");
  await expect(page.getByRole("button", { name: "Geri al", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Ürünlere git" })).toBeVisible();
  await expectNoSeriousViolations(page);
  // Start is idempotent per (job, version).
  const starts = imports.requests.filter((r) => r.path.endsWith("/start"));
  expect(starts).toHaveLength(1);
  expect(starts[0].headers["idempotency-key"]).toBeTruthy();

  await page.getByRole("link", { name: "İçe aktarma geçmişi" }).click();
  const history = page.getByRole("table", { name: "İçe aktarma geçmişi" });
  await expect(history.getByRole("link", { name: "urunler.xlsx" })).toBeVisible();
  await expect(history).toContainText("Tamamlandı");
  await expect(history).toContainText("Siz");
  await expect(history).toContainText("2 eklendi · 1 güncellendi");
});

test("mapping edit: move a field, required fields, price column needs a list, template saved and offered next time (MOCK)", async ({ page }) => {
  const api = await signedIn(page);
  const imports = api.imports!;
  await uploadProducts(page);

  // A field maps one column: choosing "Ürün adı" for "Not" takes it away from "Ürün Adı".
  await columnSelect(page, "Not").selectOption("name");
  await expect(columnSelect(page, "Ürün Adı")).toHaveValue("");
  await expect(page.getByText("“Ürün adı” alanı “Ürün Adı” kolonundan alınıp “Not” kolonuna eşlendi.")).toBeAttached();
  await columnSelect(page, "Not").selectOption("");
  await expect(page.getByText("Ürün adı: henüz eşlenmedi")).toBeAttached();
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  const alert = page.getByRole("alert").filter({ hasText: "Eşleme kaydedilemedi" });
  await expect(alert).toContainText("“Ürün adı” alanı zorunlu; bir kolonla eşleyin.");
  // The price column (suggested) is mapped, so a price list is required.
  await expect(alert).toContainText("Fiyatların yazılacağı fiyat listesini seçin.");
  expect(mappingPut(imports)).toHaveLength(0);

  await columnSelect(page, "Ürün Adı").selectOption("name");
  await page.getByLabel("Hedef fiyat listesi").selectOption(PRICE_LIST_IDS.perakende);
  await columnSelect(page, "Not").selectOption("description");
  await page.getByLabel("Aktarma modu").selectOption("create_only");
  await page.getByLabel("Bu eşlemeyi şablon olarak kaydet").check();
  await page.getByLabel("Şablon adı").fill("Tedarikçi şablonu (mock)");
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Önizleme", exact: true })).toBeVisible();
  const job = imports.jobs[0];
  expect(job.mapping).toEqual({ ...DEFAULT_MAPPING, price: "Satış Fiyatı", "attr.renk": "Renk", description: "Not" });
  expect(job.priceListId).toBe(PRICE_LIST_IDS.perakende);
  expect(job.mode).toBe("create_only");
  expect(imports.templates.map((t) => t.name)).toEqual(["Tedarikçi şablonu (mock)"]);
  // create_only: the existing SKU is skipped.
  await expect(page.getByLabel("Önizleme özeti")).toContainText("Atlanacak1");

  // Back to the mapping keeps the saved values.
  await page.getByRole("button", { name: "Eşlemeye dön" }).click();
  await expect(page.getByText("Daha önce kaydettiğiniz eşleme yüklendi.")).toBeVisible();
  await expect(columnSelect(page, "Not")).toHaveValue("description");

  // The next file with the same headers offers the template; applying it fills the mapping.
  await uploadProducts(page, "urunler-2.xlsx");
  await page.getByLabel("Bu dosyaya uyan şablon").selectOption({ label: "Tedarikçi şablonu (mock)" });
  await page.getByRole("button", { name: "Şablonu uygula" }).click();
  await expect(columnSelect(page, "Not")).toHaveValue("description");
  await expect(page.getByLabel("Aktarma modu")).toHaveValue("create_only");
});

test("preview errors: error filter, paging controls and the CSV error report download (MOCK)", async ({ page }) => {
  await signedIn(page);
  await uploadProducts(page);
  await columnSelect(page, "Satış Fiyatı").selectOption("");
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  const rows = page.getByRole("table", { name: "Önizleme satırları" });
  await expect(rows.getByText("KDV oranı sayı olmalı (ör. 20).")).toBeVisible();
  await expect(rows.getByRole("row")).toHaveCount(3); // header + 2 error rows
  await page.getByRole("button", { name: /^Tümü/ }).click();
  await expect(rows.getByRole("row")).toHaveCount(6);
  await page.getByRole("button", { name: /^Güncellenecek/ }).click();
  await expect(rows).toContainText("MCK-EXIST");
  await expect(page.getByRole("button", { name: "Sonraki" })).toBeDisabled();

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Hata raporunu indir (CSV)" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^ice-aktarma-hatalari-.*\.csv$/);
  const text = await (await file.createReadStream()).toArray().then((chunks) => Buffer.concat(chunks).toString("utf8"));
  expect(text).toContain("Ürün adı zorunlu.");
  await expect(page.getByRole("status").filter({ hasText: "Hata raporu indirildi" })).toBeVisible();
});

test("cancel: before the run nothing is written; during the run processed rows stay and it can resume (MOCK)", async ({ page }) => {
  const imports = createMockImports({ chunk: 1 });
  await signedIn(page, { imports });
  await uploadProducts(page);
  await columnSelect(page, "Satış Fiyatı").selectOption("");
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Önizleme", exact: true })).toBeVisible();

  await page.getByRole("button", { name: "3 satırı aktar" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarılıyor" })).toBeVisible();
  await page.getByRole("button", { name: "Aktarmayı iptal et" }).click();
  const dialog = page.getByRole("alertdialog", { name: "Aktarmayı iptal et" });
  await expect(dialog).toContainText("İşlenmiş satırlar kalır, kalan satırlar işlenmez.");
  await expectNoSeriousViolations(page);
  await dialog.getByRole("button", { name: "İptal et" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma iptal edildi" })).toBeVisible();
  await expect(page.getByText("İşlenmiş satırlar kaldı; kalan satırlar işlenmedi.")).toBeVisible();
  // Resume from the checkpoint.
  await page.getByRole("button", { name: "Kaldığı yerden devam et" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma tamamlandı" })).toBeVisible({ timeout: 15_000 });

  // A second upload, cancelled in the preview: nothing written.
  await uploadProducts(page, "iptal.xlsx");
  await columnSelect(page, "Satış Fiyatı").selectOption("");
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Önizleme", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Aktarmayı iptal et" }).click();
  await expect(page.getByRole("alertdialog")).toContainText("Henüz hiçbir kayıt yazılmadı.");
  await page.getByRole("alertdialog").getByRole("button", { name: "İptal et" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma iptal edildi" })).toBeVisible();
  await expect(page.getByText("Hiçbir kayıt yazılmadı.", { exact: true })).toBeVisible();
  expect(imports.jobs[0].status).toBe("cancelled");
});

test("rollback: confirmation explains archive-not-delete and conflicts; conflict rows listed; expired window hides it (MOCK)", async ({ page }) => {
  const mapping = DEFAULT_MAPPING;
  const finished = new Date(Date.now() - 2 * 86_400_000).toISOString();
  const recent = mockJob({
    fileName: "geri-al.xlsx",
    status: "completed",
    mapping,
    validatedAt: finished,
    startedAt: finished,
    finishedAt: finished,
    createdBy: USER.id,
    rollbackDeadline: new Date(Date.now() + 5 * 86_400_000).toISOString(),
    preview: { create: 2, update: 1, skip: 0, error: 2 },
    progress: { plannedRows: 3, processedRows: 3, percent: 100, created: 2, updated: 1, skipped: 0, failed: 0 },
  });
  const old = mockJob({
    fileName: "eski.xlsx",
    status: "completed",
    mapping,
    validatedAt: finished,
    startedAt: finished,
    finishedAt: finished,
    rollbackDeadline: new Date(Date.now() - 86_400_000).toISOString(),
    progress: { plannedRows: 1, processedRows: 1, percent: 100, created: 1, updated: 0, skipped: 0, failed: 0 },
  });
  const imports = createMockImports({
    jobs: [recent, old],
    rows: new Map([[recent.id, importedRows(DEFAULT_PRODUCT_FILE, mapping, ["create", "create", "error", "error", "update"])]]),
    conflictSkus: new Set(["MCK-002"]),
  });
  await signedIn(page, { imports });

  await page.goto(`/ice-aktarma/${old.id}`);
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma tamamlandı" })).toBeVisible();
  await expect(page.getByText("Geri alma süresi (7 gün) doldu.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Geri al", exact: true })).toHaveCount(0);

  await page.goto(`/ice-aktarma/${recent.id}`);
  await expect(page.getByText(/tarihine kadar bir kez geri alabilirsiniz/)).toBeVisible();
  await page.getByRole("button", { name: "Geri al", exact: true }).click();
  const dialog = page.getByRole("alertdialog", { name: "Aktarmayı geri al" });
  await expect(dialog).toContainText("2 ürün silinmez, arşivlenir");
  await expect(dialog).toContainText("dokunulmaz");
  await expect(dialog).toContainText("çakışma");
  await expect(dialog).toContainText("Stok miktarları değişmez.");
  await expectNoSeriousViolations(page);
  await dialog.getByRole("button", { name: "Geri al", exact: true }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma kısmen geri alındı" })).toBeVisible({ timeout: 15_000 });
  const rollback = imports.requests.find((r) => r.path.endsWith("/rollback"))!;
  expect(rollback.headers["if-match"]).toBe('W/"1"');
  await expect(page.getByLabel("Geri alma sonucu")).toContainText("Geri alındı2");
  await expect(page.getByLabel("Geri alma sonucu")).toContainText("Çakışma (dokunulmadı)1");
  await page.getByRole("button", { name: /^Çakışmalar/ }).click();
  const rows = page.getByRole("table", { name: "Aktarma satırları" });
  await expect(rows).toContainText("MCK-002");
  await expect(rows).toContainText("Çakışma — dokunulmadı");
  await expect(page.getByRole("button", { name: "Geri al", exact: true })).toHaveCount(0);
  await expectNoSeriousViolations(page);
});

test("rollback conflict codes: already rolled back elsewhere (409) is explained and the page refreshes (MOCK)", async ({ page }) => {
  const finished = new Date().toISOString();
  const job = mockJob({
    status: "completed",
    mapping: DEFAULT_MAPPING,
    validatedAt: finished,
    startedAt: finished,
    finishedAt: finished,
    rollbackDeadline: new Date(Date.now() + 86_400_000).toISOString(),
    progress: { plannedRows: 1, processedRows: 1, percent: 100, created: 1, updated: 0, skipped: 0, failed: 0 },
  });
  const imports = createMockImports({ jobs: [job] });
  await signedIn(page, { imports });
  await page.goto(`/ice-aktarma/${job.id}`);
  await page.getByRole("button", { name: "Geri al", exact: true }).click();
  // Someone else rolled it back meanwhile.
  job.status = "rolled_back";
  job.rollback = { requestedAt: finished, requestedBy: null, finishedAt: finished, planned: 1, rolledBack: 1, conflicts: 0, failed: 0, skipped: 0 };
  job.version += 1;
  await page.getByRole("alertdialog").getByRole("button", { name: "Geri al", exact: true }).click();
  await expect(page.getByRole("alertdialog")).toContainText("zaten geri alındı veya şu an geri alınıyor");
  await page.getByRole("alertdialog").getByRole("button", { name: "Vazgeç" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Aktarma geri alındı" })).toBeVisible();
});

test("upload errors: client pre-check (.xls), server rejection (415 macro), 429 with Retry-After; drag & drop (MOCK)", async ({ page }) => {
  const imports = createMockImports({ rejectFiles: { "makrolu.xlsx": { status: 415, code: "imports.file_macro_not_allowed" } } });
  await signedIn(page, { imports });
  await page.goto("/ice-aktarma/yeni");
  await page.getByRole("button", { name: "Devam" }).click();
  const input = page.locator("input[type=file]");

  await input.setInputFiles({ name: "eski.xls", mimeType: "application/vnd.ms-excel", buffer: Buffer.from("xls") });
  await expect(page.getByRole("alert").filter({ hasText: "Eski Excel biçimi (.xls) desteklenmiyor" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Yükle ve devam et" })).toBeDisabled();

  await input.setInputFiles({ name: "makrolu.xlsx", mimeType: XLSX_MIME, buffer: Buffer.from("PK") });
  await page.getByRole("button", { name: "Yükle ve devam et" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Makro içeren Excel dosyaları (.xlsm) kabul edilmez" })).toBeVisible();

  // Drag & drop onto the drop zone.
  const dataTransfer = await page.evaluateHandle(() => {
    const dt = new DataTransfer();
    dt.items.add(new File(["a;b"], "surukle.csv", { type: "text/csv" }));
    return dt;
  });
  await page.getByTestId("import-dropzone").dispatchEvent("drop", { dataTransfer });
  await expect(page.getByText("surukle.csv")).toBeVisible();

  imports.uploadLimit = imports.uploadsDone;
  await page.getByRole("button", { name: "Yükle ve devam et" }).click();
  const limited = page.getByRole("alert").filter({ hasText: "Yükleme sınırına ulaşıldı" });
  await expect(limited).toContainText("Bu işletme için saatlik dosya yükleme sınırına ulaşıldı. Yaklaşık 25 dakika sonra tekrar deneyin.");
  await expect(page.getByRole("button", { name: "Yükle ve devam et" })).toBeDisabled();
  await expectNoSeriousViolations(page);
});

test("validate after the raw file was purged: 409 imports.file_purged is explained (MOCK)", async ({ page }) => {
  const job = mockJob({ status: "mapped", mapping: DEFAULT_MAPPING });
  const imports = createMockImports({ jobs: [job], purged: new Set([job.id]) });
  await signedIn(page, { imports });
  await page.goto(`/ice-aktarma/${job.id}`);
  await columnSelect(page, "Satış Fiyatı").selectOption("");
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Eşleme kaydedildi ancak önizleme başlatılamadı" })).toContainText("30 gün");
});

test("permissions: the manager (stock only) is locked on the history and the wizard (MOCK)", async ({ page }) => {
  await signedIn(page, { memberships: [MANAGER] });
  await page.goto("/ice-aktarma");
  await expect(page.getByRole("heading", { name: "İçe aktarma için yetkiniz yok" })).toBeVisible();
  await expect(page.getByText("stok içe aktarma henüz kullanıma açılmadı")).toBeVisible();
  await expect(page.getByRole("link", { name: "Yeni içe aktarma" })).toHaveCount(0);
  await page.goto("/ice-aktarma/yeni");
  await expect(page.getByRole("heading", { name: "İçe aktarma için yetkiniz yok" })).toBeVisible();
  await expectNoSeriousViolations(page);
});

test("sales: no menu item and the direct link is locked (MOCK)", async ({ page }) => {
  await signedIn(page, { memberships: [SALES] });
  await page.goto("/ice-aktarma");
  await expect(page.getByRole("navigation", { name: "Ana menü" }).getByRole("link", { name: "İçe aktarma" })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "İçe aktarma için yetkiniz yok" })).toBeVisible();
});

test("price list import: the target list is chosen in step 1 and carried to the mapping (MOCK)", async ({ page }) => {
  const imports = createMockImports({
    files: { "fiyatlar.csv": { headers: ["Stok Kodu", "Birim Fiyat", "Kademe"], rows: [["MCK-EXIST", "99,90", "1"], ["YOK-1", "10", "1"]] } },
  });
  await signedIn(page, { imports });
  await page.goto("/ice-aktarma/yeni");
  await page.getByRole("radio", { name: /Fiyat listesi kalemleri/ }).check();
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Fiyatların yazılacağı fiyat listesini seçin." })).toBeVisible();
  await page.getByLabel("Hedef fiyat listesi").selectOption(PRICE_LIST_IDS.bayiA);
  await page.getByRole("button", { name: "Devam" }).click();
  await page.locator("input[type=file]").setInputFiles({ name: "fiyatlar.csv", mimeType: "text/csv", buffer: Buffer.from("Stok Kodu;Birim Fiyat") });
  await page.getByRole("button", { name: "Yükle ve devam et" }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Kolonları eşleyin" })).toBeVisible();
  await expect(page.getByLabel("Hedef fiyat listesi")).toHaveValue(PRICE_LIST_IDS.bayiA);
  await expect(columnSelect(page, "Birim Fiyat")).toHaveValue("price");
  await expect(columnSelect(page, "Kademe")).toHaveValue("minQuantity");
  await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
  await expect(page.getByLabel("Önizleme özeti")).toContainText("Güncellenecek1");
  await expect(page.getByRole("table", { name: "Önizleme satırları" })).toContainText("Ürün bulunamadı.");
  expect(imports.jobs[0].priceListId).toBe(PRICE_LIST_IDS.bayiA);
});

test.describe("mobile", () => {
  test.use({ viewport: MOBILE });

  test("wizard steps stack on a phone; mapping and preview are usable and pass axe (MOCK)", async ({ page }) => {
    await signedIn(page);
    await uploadProducts(page);
    await expect(page.getByText("Adım 3/6:")).toBeVisible();
    // No horizontal scrolling of the page.
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await expectNoSeriousViolations(page);
    await columnSelect(page, "Satış Fiyatı").selectOption("");
    await page.getByRole("button", { name: "Kaydet ve önizle" }).click();
    await expect(page.getByRole("heading", { level: 2, name: "Önizleme", exact: true })).toBeVisible();
    await expect(page.getByRole("list", { name: "Önizleme satırları" })).toContainText("Ürün adı zorunlu.");
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await expectNoSeriousViolations(page);
    await page.getByRole("link", { name: "İçe aktarma geçmişi" }).click();
    await expect(page.getByRole("list", { name: "İçe aktarma geçmişi" })).toContainText("urunler.xlsx");
    await expectNoSeriousViolations(page);
  });
});
