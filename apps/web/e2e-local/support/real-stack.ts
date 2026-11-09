import { expect, type Page, type Request } from "@playwright/test";

/**
 * Helpers for the REAL stack (no mocks): Supabase Auth + Mailpit, BayiOS API, this web app.
 * All data is synthetic and unique per run (time stamp), so runs never depend on each other.
 */
export const MAILPIT = process.env.MAILPIT_URL ?? "http://127.0.0.1:54324";
export const API_URL = process.env.E2E_API_URL ?? "http://localhost:5280";

export async function confirmationLink(email: string): Promise<string> {
  for (let attempt = 0; attempt < 40; attempt++) {
    const search = await fetch(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`);
    const { messages } = (await search.json()) as { messages: { ID: string }[] };
    if (messages.length > 0) {
      const message = (await (await fetch(`${MAILPIT}/api/v1/message/${messages[0].ID}`)).json()) as { Text: string };
      const link = message.Text.match(/https?:\/\/\S+\/verify\?\S+/)?.[0];
      if (link) return link.replace(/&amp;/g, "&");
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`No confirmation e-mail for ${email}`);
}

export interface Account {
  email: string;
  password: string;
  fullName: string;
  businessName: string;
  slug: string;
}

export function newAccount(tag: string): Account {
  const stamp = `${Date.now()}${Math.floor(Math.random() * 1000)}`;
  return {
    email: `e2e-${tag}-${stamp}@example.test`,
    password: `E2e-${tag}-Sifre-1234`,
    fullName: `E2E ${tag.toUpperCase()} Kullanıcısı`,
    businessName: `E2E Motor ${tag.toUpperCase()} ${stamp}`,
    slug: `e2e-${tag}-${stamp}`,
  };
}

/** Sign-up → confirmation e-mail (Mailpit) → onboarding with the motorcycle-parts template → /pano. */
export async function signUpAndOnboard(page: Page, account: Account) {
  await page.goto("/kayit");
  await page.getByLabel("Ad soyad").fill(account.fullName);
  await page.getByLabel("E-posta").fill(account.email);
  await page.locator("input[name=password]").fill(account.password);
  await page.locator("input[name=confirm]").fill(account.password);
  await page.getByRole("button", { name: "Hesap oluştur" }).click();
  await expect(page.getByText("doğrulama bağlantısı gönderdik")).toBeVisible();

  // Supabase /verify redirects to /auth/callback?code=… (PKCE) → onboarding.
  await page.goto(await confirmationLink(account.email));
  await expect(page).toHaveURL(/\/isletme-olustur$/);

  await page.getByLabel("İşletme adı").fill(account.businessName);
  await page.getByLabel("İşletme adresi (kısa ad)").fill(account.slug);
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByLabel(/Motosiklet yedek parça/).check();
  await page.getByRole("button", { name: "Devam" }).click();
  await page.getByLabel("Para birimi").selectOption("TRY");
  await page.getByLabel("Varsayılan KDV oranı").selectOption("20");
  await page.getByLabel("Fiyat gösterimi").selectOption("excluded");
  await expect(page.getByText("Stok eksiye düşmez")).toBeVisible(); // OD-09: fixed rule, no choice
  await page.getByRole("button", { name: "Devam" }).click();
  await expect(page.getByText(account.slug)).toBeVisible();

  const applied = page.waitForResponse((r) => r.url().endsWith("/sector-templates/motorcycle_parts/apply"));
  await page.getByRole("button", { name: "İşletmeyi oluştur" }).click();
  const applyResponse = await applied;
  expect(applyResponse.status()).toBe(200);
  expect(((await applyResponse.json()) as { template: string }).template).toBe("motorcycle_parts");

  await expect(page).toHaveURL(/\/pano$/, { timeout: 15_000 });
  await expect(page.getByTestId("session-user")).toHaveText(account.fullName);
}

/** Prints every 4xx/5xx the web app receives from the API (Problem Details), so a CI failure is diagnosable from the log. */
export function logApiErrors(page: Page) {
  page.on("response", async (response) => {
    if (!response.url().startsWith(`${API_URL}/`) || response.status() < 400) return;
    const body = await response.text().catch(() => "");
    console.log(`[api ${response.status()}] ${response.request().method()} ${response.url()} ${body.slice(0, 1000)}`);
  });
}

export interface ApiSession {
  authorization: string;
  tenantId: string;
}

/**
 * The signed-in user's own credentials, taken from a real request the web app sends to the API (Bearer token issued by
 * Supabase Auth + the active X-Tenant-Id). Used to drive steps that have no web screen yet (picking, packing, shipping).
 */
export async function captureApiSession(page: Page, navigate: () => Promise<unknown>): Promise<ApiSession> {
  const isTenantApiRequest = (r: Request) =>
    r.url().startsWith(`${API_URL}/api/v1/`) && Boolean(r.headers()["authorization"]) && Boolean(r.headers()["x-tenant-id"]);
  const request = page.waitForRequest(isTenantApiRequest);
  await navigate();
  const headers = (await request).headers();
  return { authorization: headers["authorization"], tenantId: headers["x-tenant-id"] };
}

export interface ApiResult<T> {
  status: number;
  body: T;
}

export async function api<T = unknown>(session: ApiSession, method: string, path: string, body?: unknown): Promise<ApiResult<T>> {
  const headers: Record<string, string> = { Authorization: session.authorization, "X-Tenant-Id": session.tenantId, Accept: "application/json" };
  if (method !== "GET") headers["Idempotency-Key"] = crypto.randomUUID();
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch(`${API_URL}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  return { status: response.status, body: (text ? JSON.parse(text) : null) as T };
}

/** Like {@link api} but fails the test with the Problem Details body on an unexpected status. */
export async function apiOk<T = unknown>(session: ApiSession, method: string, path: string, body?: unknown, expected = [200, 201]): Promise<T> {
  const result = await api<T>(session, method, path, body);
  expect(expected, `${method} ${path} → ${result.status}: ${JSON.stringify(result.body)}`).toContain(result.status);
  return result.body;
}
