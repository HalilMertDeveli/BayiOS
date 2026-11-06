/**
 * /davet — accepting an invitation (#129 web, threat-model §4.1).
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts + support/mock-members.ts).
 * The token is synthetic; nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, TENANT_B, USER, addSessionCookie, mockApi, mockSupabaseAuth, problem } from "./support/mock-backend";
import { mockTeam, type MockTeam } from "./support/mock-members";

test.use({ viewport: DESKTOP });

const TOKEN = "TW9ja0ludml0YXRpb25Ub2tlbi1zeW50aGV0aWMtMDE";

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

/** MOCK: the invitee already belongs to TENANT_B; the invitation is for TENANT_A (sales). */
async function setup(page: Page, { signedIn = true }: { signedIn?: boolean } = {}) {
  if (signedIn) await addSessionCookie(page.context());
  await mockSupabaseAuth(page);
  const api = await mockApi(page, { memberships: [TENANT_B] });
  const team = await mockTeam(page, api, { members: [], joinTenant: TENANT_A });
  team.invitations.push({
    id: "00000000-0000-4000-8000-1a0000000001",
    email: USER.email,
    role: "sales",
    status: "pending",
    expiresAt: new Date(Date.now() + 6 * 86_400_000).toISOString(),
    tokenCount: 1,
    createdAt: new Date().toISOString(),
    invitedBy: USER.id,
    acceptedAt: null,
    acceptedBy: null,
    revokedAt: null,
  });
  team.tokens.set(TOKEN, "00000000-0000-4000-8000-1a0000000001");
  const urls: string[] = [];
  page.on("request", (r) => urls.push(r.url()));
  return { api, team, urls };
}

const acceptCalls = (team: MockTeam) => team.requests.filter((r) => r.path === "/api/v1/invitations/accept");

test("oturum yok → giriş → /davet'e dönüş → kabul → yeni işletme etkin, ana sayfa", async ({ page }) => {
  const { team, urls } = await setup(page, { signedIn: false });
  await page.goto(`/davet#${TOKEN}`);
  await expect(page.getByRole("heading", { level: 1, name: "Ekibe davet edildiniz" })).toBeVisible();
  // The fragment is stripped at once; the token waits in sessionStorage of this tab.
  await expect(page).toHaveURL(/\/davet$/);
  await expect(page.getByRole("link", { name: "Hesap oluştur" })).toHaveAttribute("href", "/kayit?next=%2Fdavet");
  await expectNoSeriousViolations(page);
  await page.getByRole("link", { name: "Giriş yap" }).click();
  await expect(page).toHaveURL("/giris?next=%2Fdavet");
  await page.getByLabel("E-posta").fill(USER.email);
  await page.locator("input[name=password]").fill("Dogru-Sifre-1");
  await page.getByRole("button", { name: "Giriş yap" }).click();

  await expect(page).toHaveURL(/\/davet$/);
  await expect(page.getByText(`${USER.email}`)).toBeVisible();
  await page.getByRole("button", { name: "Daveti kabul et" }).click();
  await expect(page).toHaveURL(/\/pano$/);
  // The joined business is the active one, with the invited role.
  await expect(page.getByRole("combobox", { name: "Aktif işletme" })).toHaveValue(TENANT_A.tenantId);
  await expect(page.getByText("Satış", { exact: true }).first()).toBeVisible();

  const [accept] = acceptCalls(team);
  expect(accept.body).toEqual({ token: TOKEN });
  expect(accept.headers["x-tenant-id"]).toBeUndefined();
  expect(accept.headers["idempotency-key"]).toBeTruthy();
  // The token never travelled in any URL (page loads, API, Supabase) and is gone from storage.
  expect(urls.filter((u) => u.includes(TOKEN))).toEqual([]);
  const stored = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
  expect(stored).not.toContain(TOKEN);
  expect(stored).toContain(TENANT_A.tenantId);
});

test("oturum yok → kayıt: doğrulama bağlantısı /davet'e döner", async ({ page }) => {
  await setup(page, { signedIn: false });
  let redirectTo: string | null = null;
  await page.route("http://127.0.0.1:54321/auth/v1/signup**", (route) => {
    redirectTo = new URL(route.request().url()).searchParams.get("redirect_to");
    return route.fulfill({ status: 200, contentType: "application/json", headers: { "Access-Control-Allow-Origin": "*" }, body: JSON.stringify({ id: USER.id, email: USER.email }) });
  });
  await page.goto(`/davet#${TOKEN}`);
  await page.getByRole("link", { name: "Hesap oluştur" }).click();
  await expect(page).toHaveURL("/kayit?next=%2Fdavet");
  await page.getByLabel("Ad soyad").fill("Mock Davetli");
  await page.getByLabel("E-posta").fill(USER.email);
  await page.locator("input[name=password]").fill("Guclu-Sifre-1");
  await page.locator("input[name=confirm]").fill("Guclu-Sifre-1");
  await page.getByRole("button", { name: "Hesap oluştur" }).click();
  await expect(page.getByRole("status")).toContainText("daveti kabul edebilirsiniz");
  expect(redirectTo).toMatch(/\/auth\/callback\?next=%2Fdavet$/);
  expect(redirectTo).not.toContain(TOKEN);
});

const ERRORS: { status: number; code: string; title: string; action: string | null; keepsToken: boolean }[] = [
  { status: 403, code: "invitation.email_mismatch", title: "Bu davet başka bir e-posta adresi için", action: "Başka hesapla giriş yap", keepsToken: true },
  { status: 403, code: "invitation.email_not_verified", title: "E-posta adresiniz doğrulanmamış", action: "Tekrar dene", keepsToken: true },
  { status: 410, code: "invitation.expired", title: "Davetin süresi dolmuş", action: null, keepsToken: false },
  { status: 404, code: "invitation.not_found", title: "Davet geçersiz", action: null, keepsToken: false },
  { status: 409, code: "invitation.already_member", title: "Zaten bu işletmenin üyesisiniz", action: "Uygulamaya git", keepsToken: false },
  { status: 403, code: "tenant.inactive", title: "İşletme kullanılamıyor", action: null, keepsToken: true },
  { status: 403, code: "entitlement.limit_exceeded", title: "İşletmenin kullanıcı sınırı dolu", action: "Tekrar dene", keepsToken: true },
];

for (const e of ERRORS) {
  test(`kabul hatası ${e.status} ${e.code} → Türkçe açıklama`, async ({ page }) => {
    const { team } = await setup(page);
    team.acceptResult = { status: e.status, body: problem(e.status, e.code) };
    await page.goto(`/davet#${TOKEN}`);
    await page.getByRole("button", { name: "Daveti kabul et" }).click();
    await expect(page.getByRole("alert").filter({ hasText: e.title })).toBeVisible();
    if (e.action) await expect(page.getByRole(e.action === "Uygulamaya git" ? "link" : "button", { name: e.action })).toBeVisible();
    else await expect(page.getByRole("button", { name: "Tekrar dene" })).toHaveCount(0);
    const stored = await page.evaluate(() => window.sessionStorage.getItem("bayios.invitationToken"));
    expect(stored === TOKEN).toBe(e.keepsToken);
    // tenant.inactive must not flip the page into the in-app "business suspended" notice: there is no tenant yet.
    await expect(page).toHaveURL(/\/davet$/);
  });
}

test("e-posta uyuşmazlığı: çıkış yapıp davet edilen adresle giriş; davet sekmede kalır", async ({ page }) => {
  const { team } = await setup(page);
  team.acceptResult = { status: 403, body: problem(403, "invitation.email_mismatch") };
  await page.goto(`/davet#${TOKEN}`);
  await page.getByRole("button", { name: "Daveti kabul et" }).click();
  await page.getByRole("button", { name: "Başka hesapla giriş yap" }).click();
  await expect(page).toHaveURL("/giris?next=%2Fdavet");
  team.acceptResult = null;
  await page.getByLabel("E-posta").fill(USER.email);
  await page.locator("input[name=password]").fill("Dogru-Sifre-1");
  await page.getByRole("button", { name: "Giriş yap" }).click();
  await page.getByRole("button", { name: "Daveti kabul et" }).click();
  await expect(page).toHaveURL(/\/pano$/);
});

test("bağlantısız /davet: nasıl devam edileceğini anlatır", async ({ page }) => {
  await setup(page);
  await page.goto("/davet");
  await expect(page.getByText("Davet bağlantısı bulunamadı")).toBeVisible();
  await expect(page.getByRole("link", { name: "Ana sayfaya git" })).toBeVisible();
});

test("mobil: davet sayfası telefona sığar ve erişilebilir", async ({ page }) => {
  await page.setViewportSize(MOBILE);
  await setup(page);
  await page.goto(`/davet#${TOKEN}`);
  await expect(page.getByRole("button", { name: "Daveti kabul et" })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await expectNoSeriousViolations(page);
});
