/**
 * #83 self-serve setup: a brand-new user signs up, creates the business with the default (sector-neutral) template and
 * is guided through /kurulum — business info, first warehouse with an auto shelf layout, the CSV template + a product
 * added by hand, opening stock as a goods receipt, a team invitation — and the dashboard "İlk adımlar" checklist then
 * reflects that progress from the data. MOCK: Supabase Auth and the BayiOS API are faked with page.route
 * (support/mock-backend.ts); every write is checked for its Idempotency-Key / If-Match.
 */
import { readFile } from "node:fs/promises";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page, type Route } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { createMockCatalog } from "./support/mock-catalog";
import { createMockInventory } from "./support/mock-inventory";
import { USER, mockApi, mockSupabaseAuth, signedIn, type MockMembership } from "./support/mock-backend";
import { mockTeam } from "./support/mock-members";

const TENANT: MockMembership = {
  tenantId: "00000000-0000-4000-8000-0000000000d4",
  tenantName: "Deniz Kırtasiye",
  tenantSlug: "deniz-kirtasiye",
  role: "owner",
  status: "active",
};

const OWNER_ONLY = [{ userId: USER.id, displayName: USER.fullName, fullName: USER.fullName, email: USER.email, role: "owner", status: "active", createdAt: "2026-11-09T00:00:00Z" }];

const created = (route: Route) =>
  route.fulfill({
    status: 201,
    contentType: "application/json",
    headers: { "Access-Control-Allow-Origin": "*", Location: "/api/v1/tenant" },
    body: JSON.stringify({ id: TENANT.tenantId, name: TENANT.tenantName, slug: TENANT.tenantSlug, status: "active", sector: "general", defaultCurrency: "TRY", locale: "tr-TR", timeZone: "Europe/Istanbul", role: "owner" }),
  });

/** An empty business: no warehouse, no product, no stock, no order; the owner is the only member. */
function emptyBusiness() {
  const catalog = createMockCatalog({ products: 0, fields: [] });
  const inventory = createMockInventory(catalog, { warehouses: [], locations: [] });
  return { catalog, inventory };
}

const WCAG_TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

const stepHeading = (page: Page, name: string) => page.getByRole("heading", { level: 2, name });

test.describe("kurulum sihirbazı — masaüstü (mock)", () => {
  test.use({ viewport: DESKTOP });

  test("yeni kullanıcı: kayıt → işletme → kurulum → ilk ürün ve açılış stoğu → pano kontrol listesi", async ({ page }) => {
    await mockSupabaseAuth(page);
    const { catalog, inventory } = emptyBusiness();
    const api = await mockApi(page, {
      memberships: [],
      catalog,
      inventory,
      createTenant: (route) => {
        api.memberships = [TENANT];
        return created(route);
      },
    });
    const team = await mockTeam(page, api, { members: OWNER_ONLY, invitations: [] });

    // ---- Sign-up and business creation with the defaults (no industry to pick).
    await page.goto("/kayit");
    await page.getByLabel("Ad soyad").fill(USER.fullName);
    await page.getByLabel("E-posta").fill(USER.email);
    await page.locator("input[name=password]").fill("Guclu-Sifre-1");
    await page.locator("input[name=confirm]").fill("Guclu-Sifre-1");
    await page.getByRole("button", { name: "Hesap oluştur" }).click();
    await expect(page).toHaveURL("/isletme-olustur");
    await page.getByLabel("İşletme adı").fill(TENANT.tenantName);
    for (let i = 0; i < 3; i++) await page.getByRole("button", { name: "Devam" }).click();
    await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();

    // ---- /kurulum step 1: business info (optional fields), saved with If-Match.
    await expect(page).toHaveURL("/kurulum");
    await expect(page.getByRole("heading", { level: 1, name: "İşletmenizi kuralım" })).toBeVisible();
    await expect(stepHeading(page, "İşletme bilgileri")).toBeVisible();
    await expect(page.getByLabel("İşletme adı")).toHaveValue(TENANT.tenantName);
    await page.getByLabel("Vergi no / T.C. kimlik no").fill("123");
    await page.getByRole("button", { name: "Kaydet ve devam et" }).click();
    await expect(page.getByText("Vergi numarası 10 haneli (VKN)")).toBeVisible();
    await page.getByLabel("Vergi no / T.C. kimlik no").fill("1234567890");
    await page.getByLabel("Telefon").fill("0212 555 00 00");
    await page.getByRole("button", { name: "Kaydet ve devam et" }).click();

    // ---- Step 2: first warehouse with an automatic shelf layout A-01 … A-03.
    await expect(page).toHaveURL("/kurulum?adim=depo");
    await expect(stepHeading(page, "İlk deponuz")).toBeFocused();
    await expect(page.getByLabel("Depo adı")).toHaveValue("Merkez Depo");
    await expect(page.getByLabel("Basit raf düzeni oluştur")).toBeChecked();
    await page.getByLabel("Raf sayısı").fill("3");
    await expect(page.getByText("Oluşacak raflar: A-01 … A-03")).toBeVisible();
    await page.getByRole("button", { name: "Depoyu oluştur" }).click();

    // ---- Step 3: products — the CSV template downloads; one product is added by hand.
    await expect(page).toHaveURL("/kurulum?adim=urunler");
    await expect(stepHeading(page, "Ürünleriniz")).toBeFocused();
    const [download] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Şablonu indir (CSV)" }).click()]);
    expect(download.suggestedFilename()).toBe("bayios-urun-sablonu.csv");
    const csv = await readFile((await download.path())!, "utf8");
    expect(csv.split(/\r?\n/)[0]).toContain("Stok Kodu;Barkod;Ürün Adı");
    await expect(page.getByRole("link", { name: "İçe aktarmayı başlat" })).toHaveAttribute("href", "/ice-aktarma/yeni");
    await page.getByLabel("Ürün adı").fill("Kurşun Kalem HB");
    await expect(page.getByLabel("Stok kodu (SKU)")).toHaveValue("KURSUN-KALEM-HB");
    await page.getByLabel("Barkod").fill("2000000000017");
    await page.getByRole("button", { name: "Ürünü ekle" }).click();
    await expect(page.getByRole("list", { name: "Eklenen ürünler" })).toContainText("KURSUN-KALEM-HB");
    await expect(page.getByLabel("Ürün adı")).toBeFocused();
    await page.getByRole("button", { name: "Devam" }).click();

    // ---- Step 4: opening stock as a goods receipt onto shelf A-02.
    await expect(page).toHaveURL("/kurulum?adim=stok");
    await expect(stepHeading(page, "Açılış stoğu")).toBeFocused();
    await page.getByLabel("KURSUN-KALEM-HB miktarı").fill("25");
    await page.getByLabel("KURSUN-KALEM-HB rafı").selectOption({ label: "A-02" });
    await page.getByRole("button", { name: "Stoğu kaydet" }).click();
    await expect(page.getByText("1 ürün, toplam 25 birim stoğa girdi.").first()).toBeVisible();
    await page.getByRole("button", { name: "Devam" }).click();

    // ---- Step 5: team invitation with plain-language roles; the link is shown once.
    await expect(page).toHaveURL("/kurulum?adim=ekip");
    await expect(page.getByText("Depoda çalışır: mal kabul, toplama, paketleme ve sevkiyat.", { exact: false })).toBeVisible();
    const invite = page.getByRole("form", { name: "Ekip daveti" });
    await invite.getByLabel("E-posta").fill("depo@example.test");
    await invite.getByLabel(/^Depo personeli/).check();
    await page.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();
    const linkDialog = page.getByRole("alertdialog", { name: "Davet bağlantısı hazır" });
    await expect(linkDialog).toBeVisible();
    await linkDialog.getByRole("button", { name: "Tamam" }).click();
    await expect(page.getByRole("list", { name: "Bekleyen davetler" })).toContainText("depo@example.test");
    await page.getByRole("button", { name: "Devam" }).click();

    // ---- Finish: summary from data, then the dashboard checklist (4 of 5: no order yet).
    await expect(page).toHaveURL("/kurulum?adim=bitti");
    await expect(stepHeading(page, "Kurulum özeti")).toBeVisible();
    await page.getByRole("link", { name: "Panoya git" }).click();
    await expect(page).toHaveURL("/pano");
    const checklist = page.getByRole("region", { name: "İlk adımlar" });
    await expect(checklist).toContainText("5 adımdan 4 tanesi tamamlandı.");
    await expect(checklist.getByRole("link", { name: "İlk siparişi aç" })).toHaveAttribute("href", "/siparisler/yeni");
    await checklist.getByRole("button", { name: "İlk adımlar listesini kapat" }).click();
    await expect(checklist).toHaveCount(0);
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: "Pano" })).toBeVisible();
    await expect(page.getByRole("region", { name: "İlk adımlar" })).toHaveCount(0);

    // ---- Every write went through the API with its guards.
    const tenantPatch = api.requests.find((r) => r.method === "PATCH" && r.path === "/api/v1/tenant" && (r.body as { taxNumber?: string })?.taxNumber);
    expect(tenantPatch?.headers["if-match"]).toBeTruthy();
    expect(tenantPatch?.body).toEqual({ phone: "0212 555 00 00", taxNumber: "1234567890" });
    const warehousePost = api.requests.find((r) => r.method === "POST" && r.path === "/api/v1/warehouses");
    expect(warehousePost?.body).toEqual({ code: "MERKEZ", name: "Merkez Depo", usesLocations: true });
    expect(warehousePost?.headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    const shelfPosts = api.requests.filter((r) => r.method === "POST" && /\/locations$/.test(r.path));
    expect(shelfPosts.map((r) => (r.body as { code: string }).code)).toEqual(["A-01", "A-02", "A-03"]);
    expect(new Set(shelfPosts.map((r) => r.headers["idempotency-key"])).size).toBe(3);
    const productPost = api.requests.find((r) => r.method === "POST" && r.path === "/api/v1/products");
    expect(productPost?.body).toMatchObject({ name: "Kurşun Kalem HB", sku: "KURSUN-KALEM-HB", barcode: "2000000000017", taxRate: 20, status: "active" });
    expect(productPost?.headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    const receipt = api.requests.find((r) => r.method === "POST" && r.path === "/api/v1/inventory/receipts");
    expect(receipt?.headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
    expect((receipt?.body as { reference: string; lines: { quantity: number }[] }).reference).toBe("Açılış stoğu");
    expect((receipt?.body as { lines: { quantity: number }[] }).lines).toHaveLength(1);
    expect(inventory.balances.reduce((s, b) => s + b.onHand, 0)).toBe(25);
    expect(team.invitations.map((i) => [i.email, i.role])).toEqual([["depo@example.test", "warehouse_staff"]]);
  });

  test("her adım atlanabilir; ilerleme veriden okunur ve sonradan devam edilir", async ({ page }) => {
    const { catalog, inventory } = emptyBusiness();
    const api = await signedIn(page, { memberships: [TENANT], catalog, inventory });
    await mockTeam(page, api, { members: OWNER_ONLY, invitations: [] });

    await page.goto("/kurulum");
    for (const heading of ["İşletme bilgileri", "İlk deponuz", "Ürünleriniz", "Açılış stoğu", "Ekibinizi davet edin"]) {
      await expect(stepHeading(page, heading)).toBeVisible();
      await page.getByRole("button", { name: "Bu adımı atla" }).click();
    }
    await expect(stepHeading(page, "Kurulum özeti")).toBeVisible();
    await expect(page.getByRole("heading", { level: 3, name: "Deponuzu tanımlayın (yapılmadı)" })).toBeVisible();
    // Nothing was written by skipping.
    expect(api.requests.some((r) => r.method !== "GET")).toBe(false);

    // Stock without a warehouse / product: the step explains what is missing and links back.
    await page.goto("/kurulum?adim=stok");
    await expect(page.getByRole("heading", { name: "Önce bir depo tanımlayın" })).toBeVisible();
    await page.getByRole("link", { name: "Depo adımına git" }).click();
    await expect(page).toHaveURL("/kurulum?adim=depo");
    await page.getByLabel("Raf kullanmıyorum").check();
    await expect(page.getByLabel("Raf sayısı")).toHaveCount(0);
    await page.getByRole("button", { name: "Depoyu oluştur" }).click();
    await expect(page).toHaveURL("/kurulum?adim=urunler");
    await page.goto("/kurulum?adim=stok");
    await expect(page.getByRole("heading", { name: "Henüz ürün yok" })).toBeVisible();

    // The warehouse step now shows the existing warehouse (resumed setup) instead of creating a second one.
    await page.goto("/kurulum?adim=depo");
    await expect(page.getByText("Raf kullanmıyor: stok depo düzeyinde tutulur.", { exact: false })).toBeVisible();
    await expect(page.getByRole("button", { name: "Depoyu oluştur" })).toHaveCount(0);
    expect(api.requests.filter((r) => r.method === "POST" && r.path === "/api/v1/warehouses")).toHaveLength(1);
    await expect(page.getByRole("navigation", { name: "Kurulum adımları" }).getByRole("link", { name: /Adım 2 \(tamamlandı\)/ })).toBeVisible();
  });

  test("satış rolü kurulumu açamaz ve panoda kontrol listesi görmez", async ({ page }) => {
    await signedIn(page, { memberships: [{ ...TENANT, role: "sales" }] });
    await page.goto("/kurulum");
    await expect(page.getByRole("heading", { name: "Kurulumu işletme sahibi veya yöneticisi yapar" })).toBeVisible();
    await page.goto("/pano");
    await expect(page.getByRole("heading", { level: 1, name: "Pano" })).toBeVisible();
    await expect(page.getByRole("region", { name: "İlk adımlar" })).toHaveCount(0);
  });

  test("/kurulum has no critical/serious axe violations (desktop)", async ({ page }) => {
    const { catalog, inventory } = emptyBusiness();
    const api = await signedIn(page, { memberships: [TENANT], catalog, inventory });
    await mockTeam(page, api, { members: OWNER_ONLY, invitations: [] });
    for (const step of ["", "?adim=depo", "?adim=urunler", "?adim=ekip"]) {
      await page.goto(`/kurulum${step}`);
      await expect(page.getByRole("heading", { level: 2 }).first()).toBeVisible();
      await expect(page.getByRole("status").filter({ hasText: /yükleniyor/ })).toHaveCount(0);
      await expectNoSeriousViolations(page);
    }
  });
});

test.describe("kurulum sihirbazı — mobil 360/390 (mock)", () => {
  for (const width of [360, MOBILE.width]) {
    test(`depo + ürün + stok adımları ${width} px genişlikte yatay kaydırmasız çalışır`, async ({ page }) => {
      await page.setViewportSize({ width, height: MOBILE.height });
      const { catalog, inventory } = emptyBusiness();
      const api = await signedIn(page, { memberships: [TENANT], catalog, inventory });
      await mockTeam(page, api, { members: OWNER_ONLY, invitations: [] });

      await page.goto("/kurulum?adim=depo");
      await expect(page.getByText("Adım 2/6:")).toBeVisible();
      await page.getByRole("button", { name: "Depoyu oluştur" }).click();
      await expect(page).toHaveURL("/kurulum?adim=urunler");
      await page.getByLabel("Ürün adı").fill("Defter A4");
      await page.getByRole("button", { name: "Ürünü ekle" }).click();
      await expect(page.getByRole("list", { name: "Eklenen ürünler" })).toContainText("DEFTER-A4");
      await page.getByRole("button", { name: "Devam" }).click();
      await page.getByLabel("DEFTER-A4 miktarı").fill("12");
      await page.getByRole("button", { name: "Stoğu kaydet" }).click();
      await expect(page.getByText("1 ürün, toplam 12 birim stoğa girdi.").first()).toBeVisible();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
      await expectNoSeriousViolations(page);
    });
  }
});
