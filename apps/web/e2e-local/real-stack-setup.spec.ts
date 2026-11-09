import { expect, test } from "@playwright/test";
import { apiOk, captureApiSession, confirmationLink, logApiErrors, newAccount } from "./support/real-stack";

/**
 * #83 self-serve setup on the REAL stack (Supabase Auth + Mailpit + API + PostgreSQL, nothing mocked): a brand-new
 * user signs up, confirms the e-mail, creates the business with the default (sector-neutral) template and walks the
 * guided setup — business info, first warehouse with an automatic shelf layout, a product added by hand, opening
 * stock as a goods receipt on a shelf — then the dashboard checklist reflects it. Ledger and balances are checked
 * through the API with the user's own token.
 */
test("yeni kullanıcı: kayıt → işletme (sade şablon) → kurulum sihirbazı → ilk ürün ve açılış stoğu (gerçek API)", async ({ page }) => {
  test.setTimeout(120_000);
  logApiErrors(page);
  const account = newAccount("kur");
  const sku = `KALEM-${Date.now().toString(36).toUpperCase()}`;

  await page.goto("/kayit");
  await page.getByLabel("Ad soyad").fill(account.fullName);
  await page.getByLabel("E-posta").fill(account.email);
  await page.locator("input[name=password]").fill(account.password);
  await page.locator("input[name=confirm]").fill(account.password);
  await page.getByRole("button", { name: "Hesap oluştur" }).click();
  await expect(page.getByText("doğrulama bağlantısı gönderdik")).toBeVisible();
  await page.goto(await confirmationLink(account.email));
  await expect(page).toHaveURL(/\/isletme-olustur$/);

  // Business with every default: the plain starter template is pre-selected, no industry to choose.
  await page.getByLabel("İşletme adı").fill(account.businessName);
  await page.getByLabel("İşletme adresi (kısa ad)").fill(account.slug);
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByLabel(/Sade başlangıç/)).toBeChecked();
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByRole("button", { name: "Devam" }).click();
  const applied = page.waitForResponse((r) => r.url().endsWith("/sector-templates/general/apply"));
  await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();
  expect((await applied).status()).toBe(200);

  // ---- Business info (PATCH /tenant with If-Match).
  await expect(page).toHaveURL(/\/kurulum$/, { timeout: 15_000 });
  await expect(page.getByLabel("İşletme adı")).toHaveValue(account.businessName);
  await page.getByLabel("Telefon").fill("0212 555 00 00");
  await page.getByLabel("Vergi no / T.C. kimlik no").fill("1234567890");
  const profileSaved = page.waitForResponse((r) => r.url().endsWith("/api/v1/tenant") && r.request().method() === "PATCH");
  await page.getByRole("button", { name: "Kaydet ve devam et" }).click();
  expect((await profileSaved).status()).toBe(200);

  // ---- First warehouse with shelves A-01 … A-03.
  await expect(page).toHaveURL(/\/kurulum\?adim=depo$/);
  await page.getByLabel("Raf sayısı").fill("3");
  await page.getByRole("button", { name: "Depoyu oluştur" }).click();

  // ---- A product by hand (simple product: SKU at product level, the tenant's default VAT).
  await expect(page).toHaveURL(/\/kurulum\?adim=urunler$/, { timeout: 15_000 });
  await page.getByLabel("Ürün adı").fill("Kurşun Kalem HB");
  await page.getByLabel("Stok kodu (SKU)").fill(sku);
  await page.getByRole("button", { name: "Ürünü ekle" }).click();
  await expect(page.getByRole("list", { name: "Eklenen ürünler" })).toContainText(sku);
  await page.getByRole("button", { name: "Devam" }).click();

  // ---- Opening stock: 25 onto shelf A-02 as a goods receipt.
  await expect(page).toHaveURL(/\/kurulum\?adim=stok$/);
  // A reload also proves the step is resumable from the URL; its reads give us the user's own token.
  const session = await captureApiSession(page, () => page.reload());
  await page.getByLabel(`${sku} miktarı`).fill("25");
  await page.getByLabel(`${sku} rafı`).selectOption({ label: "A-02" });
  const received = page.waitForResponse((r) => r.url().endsWith("/api/v1/inventory/receipts") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Stoğu kaydet" }).click();
  expect((await received).status()).toBe(201);
  await expect(page.getByText("1 ürün, toplam 25 birim stoğa girdi.").first()).toBeVisible();
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByRole("button", { name: "Bu adımı atla" }).click(); // team: later
  await expect(page).toHaveURL(/\/kurulum\?adim=bitti$/);
  await page.screenshot({ path: "test-results/real-stack-setup-finish.png", fullPage: true });

  // ---- What the API holds now (the user's own token and tenant).
  type Tenant = { phone: string | null; taxNumber: string | null; sector: string };
  const tenant = await apiOk<Tenant>(session, "GET", "/api/v1/tenant");
  expect(tenant).toMatchObject({ phone: "0212 555 00 00", taxNumber: "1234567890", sector: "general" });
  type Warehouses = { items: { id: string; code: string; usesLocations: boolean; isDefault: boolean }[] };
  const { items: warehouses } = await apiOk<Warehouses>(session, "GET", "/api/v1/warehouses");
  expect(warehouses).toHaveLength(1);
  expect(warehouses[0]).toMatchObject({ code: "MERKEZ", usesLocations: true, isDefault: true });
  const { items: shelves } = await apiOk<{ items: { id: string; code: string }[] }>(session, "GET", `/api/v1/warehouses/${warehouses[0].id}/locations`);
  expect(shelves.map((l) => l.code)).toEqual(["A-01", "A-02", "A-03"]);
  type Balances = { items: { onHand: number | string; available: number | string; locationId: string | null }[] };
  const balances = await apiOk<Balances>(session, "GET", `/api/v1/inventory/balances?q=${encodeURIComponent(sku)}&limit=10`);
  expect(balances.items).toHaveLength(1);
  expect(Number(balances.items[0].onHand)).toBe(25);
  expect(balances.items[0].locationId).toBe(shelves.find((l) => l.code === "A-02")!.id);
  type Movements = { items: { type: string; quantity: number | string }[] };
  const movements = await apiOk<Movements>(session, "GET", "/api/v1/inventory/movements?limit=10");
  expect(movements.items.map((m) => [m.type, Number(m.quantity)])).toEqual([["receipt", 25]]);

  // ---- Dashboard checklist from the data: warehouse, product and stock done; team and first order open.
  await page.getByRole("link", { name: "Panoya git" }).click();
  const checklist = page.getByRole("region", { name: "İlk adımlar" });
  await expect(checklist).toContainText("5 adımdan 3 tanesi tamamlandı.");
  await expect(checklist.getByRole("link", { name: "Ekibi davet et" })).toBeVisible();
  await page.screenshot({ path: "test-results/real-stack-setup-pano.png", fullPage: true });
});
