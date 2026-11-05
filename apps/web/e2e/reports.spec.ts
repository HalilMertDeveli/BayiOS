/**
 * Raporlar (#53 web): hub, six reports, date presets, filters, keyset paging, CSV/XLSX export and its limits, roles.
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts, support/mock-reports.ts);
 * every number is synthetic. Nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type MockMembership } from "./support/mock-backend";
import { createMockReports, type MockReportId } from "./support/mock-reports";

test.use({ viewport: DESKTOP });

const SALES: MockMembership = { ...TENANT_A, role: "sales" };
const STAFF: MockMembership = { ...TENANT_A, role: "warehouse_staff" };

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const istanbulToday = () => new Date(Date.now() + 3 * 60 * 60 * 1000).toISOString().slice(0, 10);
const addDays = (day: string, n: number) => new Date(Date.parse(`${day}T00:00:00Z`) + n * 86_400_000).toISOString().slice(0, 10);

type Api = Awaited<ReturnType<typeof signedIn>>;
const reads = (api: Api, id: MockReportId) => api.reports!.requests.filter((r) => r.method === "GET" && r.path === `/api/v1/reports/${id}`);
const exportsOf = (api: Api, id: MockReportId) => api.reports!.requests.filter((r) => r.method === "POST" && r.path === `/api/v1/reports/${id}/export`);

const REPORT_PAGES: { slug: string; title: string; marker: (page: Page) => ReturnType<Page["getByRole"]> }[] = [
  { slug: "siparisler", title: "Sipariş durumları", marker: (p) => p.getByRole("table", { name: "Duruma göre siparişler" }) },
  { slug: "en-cok-siparis-edilenler", title: "En çok sipariş edilen ürünler", marker: (p) => p.getByRole("cell", { name: "Çok satan ürün 1 (mock)" }) },
  { slug: "kritik-stok", title: "Kritik stok", marker: (p) => p.getByRole("cell", { name: "KRT-0001" }) },
  { slug: "stok-hareketleri", title: "Stok hareketleri", marker: (p) => p.getByRole("table", { name: "Hareket tipine göre toplamlar" }) },
  { slug: "toplama-gorevleri", title: "Toplama görevleri", marker: (p) => p.getByRole("region", { name: "Toplama görevi özeti" }) },
  { slug: "ai-kullanimi", title: "AI asistan kullanımı", marker: (p) => p.getByRole("region", { name: "AI kullanım özeti" }) },
];

test("hub: owner sees all six reports; every report renders with data and passes axe (MOCK)", async ({ page }) => {
  await signedIn(page);
  await page.goto("/raporlar");
  await expect(page.getByRole("heading", { level: 1, name: "Raporlar" })).toBeVisible();
  const list = page.getByRole("list", { name: "Raporlar" });
  await expect(list.getByRole("listitem")).toHaveCount(6);
  await expectNoSeriousViolations(page);

  for (const report of REPORT_PAGES) {
    await page.goto(`/raporlar/${report.slug}`);
    await expect(page.getByRole("heading", { level: 1, name: report.title })).toBeVisible();
    await expect(report.marker(page).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "CSV indir" })).toBeEnabled();
    await expectNoSeriousViolations(page);
  }
});

test("orders: default last 30 days, presets and a custom range drive the request; chart + amounts (MOCK)", async ({ page }) => {
  const api = await signedIn(page);
  const today = istanbulToday();
  await page.goto("/raporlar/siparisler");
  await expect(page.getByRole("table", { name: "Duruma göre siparişler" })).toBeVisible();
  expect(new URLSearchParams(reads(api, "orders")[0].search).get("from")).toBe(addDays(today, -29));
  await expect(page.getByRole("button", { name: "Son 30 gün" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("img", { name: "Günlük sipariş sayısı grafiği" })).toBeVisible();
  await expect(page.getByRole("columnheader", { name: "Genel toplam" }).first()).toBeVisible();
  await expect(page.getByText("₺").first()).toBeVisible();

  await page.getByRole("button", { name: "Son 7 gün" }).click();
  await expect(page).toHaveURL(new RegExp(`baslangic=${addDays(today, -6)}&bitis=${today}`));
  await expect.poll(() => reads(api, "orders").at(-1)?.search).toContain(`from=${addDays(today, -6)}`);

  await page.getByRole("combobox", { name: "Sipariş durumu" }).selectOption("approved");
  await expect.poll(() => new URLSearchParams(reads(api, "orders").at(-1)?.search).get("status")).toBe("approved");

  // A custom range in the wrong order is caught before any request.
  const before = reads(api, "orders").length;
  await page.getByLabel("Başlangıç").fill(today);
  await page.getByLabel("Bitiş").fill(addDays(today, -3));
  await page.getByRole("button", { name: "Uygula" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Başlangıç tarihi bitiş tarihinden sonra olamaz." })).toBeVisible();
  expect(reads(api, "orders")).toHaveLength(before);

  // A URL with a too-long range explains itself instead of requesting.
  await page.goto(`/raporlar/siparisler?baslangic=${addDays(today, -400)}&bitis=${today}`);
  await expect(page.getByRole("heading", { name: "Tarih aralığını düzeltin" })).toBeVisible();
  await expect(page.getByText("Tarih aralığı en fazla 366 gün olabilir.").first()).toBeVisible();
  await expect(page.getByRole("button", { name: "CSV indir" })).toBeDisabled();
});

test("amountsVisible=false: no money columns, a note instead (MOCK)", async ({ page }) => {
  await signedIn(page, { reports: createMockReports({ hideAmounts: true }) });
  await page.goto("/raporlar/en-cok-siparis-edilenler");
  await expect(page.getByRole("cell", { name: "Çok satan ürün 1 (mock)" })).toBeVisible();
  await expect(page.getByRole("columnheader", { name: "Tutar (KDV hariç)" })).toHaveCount(0);
  await expect(page.getByText("Tutarlar rolünüz için gizli")).toBeVisible();
  await expect(page.getByText("₺")).toHaveCount(0);
});

test("critical stock: threshold + warehouse filters, server keyset paging with 'Daha fazla yükle' (MOCK)", async ({ page }) => {
  const api = await signedIn(page);
  await page.goto("/raporlar/kritik-stok");
  const table = page.getByRole("table", { name: "Kritik stok" });
  await expect(table.getByRole("row")).toHaveCount(101); // header + 100
  expect(new URLSearchParams(reads(api, "critical-stock")[0].search).get("limit")).toBe("100");
  await page.getByRole("button", { name: "Daha fazla yükle" }).click();
  await expect(table.getByRole("row")).toHaveCount(131);
  await expect(page.getByRole("button", { name: "Daha fazla yükle" })).toHaveCount(0);
  expect(new URLSearchParams(reads(api, "critical-stock")[1].search).get("cursor")).toBeTruthy();

  await page.getByRole("textbox", { name: "Kritik eşik" }).fill("2,5");
  await page.getByRole("button", { name: "Uygula" }).click();
  await expect(page).toHaveURL(/esik=2\.5/);
  await expect.poll(() => new URLSearchParams(reads(api, "critical-stock").at(-1)?.search).get("threshold")).toBe("2.5");
  await page.getByRole("combobox", { name: "Depo" }).selectOption({ label: "Ana Depo (mock)" });
  await expect.poll(() => new URLSearchParams(reads(api, "critical-stock").at(-1)?.search).get("warehouseId")).toBeTruthy();

  await page.getByRole("textbox", { name: "Kritik eşik" }).fill("abc");
  await page.getByRole("button", { name: "Uygula" }).click();
  await expect(page.getByText("0 veya daha büyük, en fazla 3 ondalıklı bir sayı girin.")).toBeVisible();
});

test("export CSV and XLSX: Idempotency-Key, report filters in the body, file download with a fallback name (MOCK)", async ({ page }) => {
  const api = await signedIn(page);
  const today = istanbulToday();
  await page.goto("/raporlar/siparisler");
  await expect(page.getByRole("table", { name: "Duruma göre siparişler" })).toBeVisible();

  const [csv] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "CSV indir" }).click()]);
  // Content-Disposition is not exposed cross-origin (as with the real API): the web builds the API's file name.
  expect(csv.suggestedFilename()).toBe(`siparisler-${addDays(today, -29)}_${today}.csv`);
  await expect(page.getByText(`CSV dosyası indirildi: siparisler-${addDays(today, -29)}_${today}.csv`)).toBeVisible();
  const [first] = exportsOf(api, "orders");
  expect(first.body).toEqual({ format: "csv", from: addDays(today, -29), to: today });
  expect(first.headers["idempotency-key"]).toBeTruthy();

  const [xlsx] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Excel indir" }).click()]);
  expect(xlsx.suggestedFilename()).toBe(`siparisler-${addDays(today, -29)}_${today}.xlsx`);
  const second = exportsOf(api, "orders")[1];
  expect(second.body).toMatchObject({ format: "xlsx" });
  expect(second.headers["idempotency-key"]).not.toBe(first.headers["idempotency-key"]);

  // Critical stock sends its own filters (threshold, no dates).
  await page.goto("/raporlar/kritik-stok?esik=3");
  await expect(page.getByRole("cell", { name: "KRT-0001" })).toBeVisible();
  const [critical] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "CSV indir" }).click()]);
  expect(critical.suggestedFilename()).toBe(`kritik-stok-${today}.csv`);
  expect(exportsOf(api, "critical-stock")[0].body).toEqual({ format: "csv", threshold: "3" });
});

test("export: the name from Content-Disposition is used when the browser may read it (MOCK)", async ({ page }) => {
  await signedIn(page, { reports: createMockReports({ exposeContentDisposition: true }) });
  await page.goto("/raporlar/stok-hareketleri");
  await expect(page.getByRole("table", { name: "Hareket tipine göre toplamlar" })).toBeVisible();
  const [file] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Excel indir" }).click()]);
  expect(file.suggestedFilename()).toMatch(/^MOCK-stok-hareketleri-\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.xlsx$/);
});

test("export limits: 429 shows the hourly limit with the Retry-After wait; 422 too many rows (MOCK)", async ({ page }) => {
  const reports = createMockReports({ exportLimit: 0, retryAfterSeconds: 1200, tooLarge: new Set(["picking-tasks"]) });
  await signedIn(page, { reports });
  await page.goto("/raporlar/en-cok-siparis-edilenler");
  await expect(page.getByRole("cell", { name: "Çok satan ürün 1 (mock)" })).toBeVisible();
  await page.getByRole("button", { name: "CSV indir" }).click();
  const alert = page.getByRole("alert").filter({ hasText: "Dosya indirilemedi" });
  await expect(alert).toContainText("Saatlik dışa aktarma sınırına ulaşıldı (işletme başına saatte 10 dosya). 20 dakika sonra tekrar deneyin.");
  await expect(alert).toBeFocused();
  await expectNoSeriousViolations(page);

  reports.exportLimit = 10;
  await page.goto("/raporlar/toplama-gorevleri");
  await expect(page.getByRole("region", { name: "Toplama görevi özeti" })).toBeVisible();
  await page.getByRole("button", { name: "Excel indir" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Dosya indirilemedi" })).toContainText("20.000 satır sınırını aşıyor");
});

test("empty and error states (MOCK)", async ({ page }) => {
  const reports = createMockReports({ empty: new Set(["orders", "critical-stock"]), failNext: new Set(["stock-movements"]) });
  await signedIn(page, { reports });
  await page.goto("/raporlar/siparisler");
  await expect(page.getByRole("heading", { name: "Bu aralıkta sipariş yok" })).toBeVisible();
  await page.goto("/raporlar/kritik-stok");
  await expect(page.getByRole("heading", { name: "Kritik stokta ürün yok" })).toBeVisible();
  await page.goto("/raporlar/stok-hareketleri");
  await expect(page.getByRole("heading", { name: "Rapor yüklenemedi" })).toBeVisible();
  await page.getByRole("button", { name: "Tekrar dene" }).click();
  await expect(page.getByRole("table", { name: "Hareket tipine göre toplamlar" })).toBeVisible();
});

test("sales: only the order reports; others explain the restriction (MOCK footnote 4)", async ({ page }) => {
  const api = await signedIn(page, { memberships: [SALES] });
  await page.goto("/raporlar");
  const list = page.getByRole("list", { name: "Raporlar" });
  await expect(list.getByRole("listitem")).toHaveCount(2);
  await expect(list.getByRole("link", { name: /Sipariş durumları/ })).toBeVisible();
  await expect(list.getByRole("link", { name: /En çok sipariş edilen ürünler/ })).toBeVisible();
  await expect(page.getByText("Rolünüz yalnızca sipariş raporlarını görebilir.")).toBeVisible();

  await page.goto("/raporlar/kritik-stok");
  await expect(page.getByRole("heading", { name: "Bu raporu görme yetkiniz yok" })).toBeVisible();
  await page.goto("/raporlar/ai-kullanimi");
  await expect(page.getByRole("heading", { name: "Bu raporu görme yetkiniz yok" })).toBeVisible();
  expect(reads(api, "critical-stock")).toHaveLength(0);
  expect(reads(api, "ai-usage")).toHaveLength(0);

  await page.goto("/raporlar/siparisler");
  await expect(page.getByRole("table", { name: "Duruma göre siparişler" })).toBeVisible();
  await expect(page.getByRole("button", { name: "CSV indir" })).toBeVisible();
});

test("warehouse staff: no Raporlar in the menu, the hub explains (MOCK)", async ({ page }) => {
  await signedIn(page, { memberships: [STAFF] });
  await page.goto("/raporlar");
  await expect(page.getByRole("heading", { name: "Raporları görme yetkiniz yok" })).toBeVisible();
});

test.describe("mobile", () => {
  test.use({ viewport: MOBILE });

  test("hub and a report with chart and export work on a phone and pass axe (MOCK)", async ({ page }) => {
    await signedIn(page);
    await page.goto("/raporlar");
    await expect(page.getByRole("list", { name: "Raporlar" }).getByRole("listitem")).toHaveCount(6);
    await expectNoSeriousViolations(page);
    await page.getByRole("link", { name: /Toplama görevleri/ }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Toplama görevleri" })).toBeVisible();
    await expect(page.getByRole("img", { name: "Günlük tamamlanan görev grafiği" })).toBeVisible();
    const [file] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "CSV indir" }).click()]);
    expect(file.suggestedFilename()).toMatch(/^toplama-gorevleri-.*\.csv$/);
    await expectNoSeriousViolations(page);
  });
});
