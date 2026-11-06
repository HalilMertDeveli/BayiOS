import { expect, test } from "@playwright/test";
import { confirmationLink, logApiErrors, newAccount, signUpAndOnboard } from "./support/real-stack";

/**
 * REAL END-TO-END — no mocks (#129): the owner invites a colleague as "Satış"; the colleague opens the link signed out,
 * signs up with that address, confirms it through Mailpit, comes back to /davet in the same tab, accepts, and lands in
 * the owner's business with the invited role. Needs the real stack (`bash infra/ci/e2e-stack.sh up`, apps/web/README.md).
 * All accounts and names are synthetic and unique per run.
 */
test("davet: sahip davet eder → davetli kayıt olur, doğrular, kabul eder → işletmede Satış rolüyle", async ({ browser, page }) => {
  test.setTimeout(120_000);
  logApiErrors(page);
  const owner = newAccount("davetci");
  const invitee = newAccount("davetli");

  await signUpAndOnboard(page, owner);

  // Owner: Ayarlar → Ekip → invite as sales; the link is shown once.
  await page.goto("/ayarlar/ekip");
  await expect(page.getByRole("heading", { level: 1, name: "Ekip" })).toBeVisible();
  await expect(page.getByRole("table", { name: "Ekip üyeleri" })).toContainText(owner.email);
  await page.getByRole("button", { name: "Kişi davet et" }).click();
  const dialog = page.getByRole("dialog", { name: "Kişi davet et" });
  await dialog.getByRole("textbox", { name: "E-posta" }).fill(invitee.email);
  await dialog.getByRole("combobox", { name: "Rol" }).selectOption("sales");
  const created = page.waitForResponse((r) => r.url().endsWith("/api/v1/members/invitations") && r.request().method() === "POST");
  await dialog.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();
  expect((await created).status()).toBe(201);
  const linkDialog = page.getByRole("alertdialog", { name: "Davet bağlantısı hazır" });
  const link = await linkDialog.getByRole("textbox", { name: "Davet bağlantısı" }).inputValue();
  expect(link).toMatch(/\/davet#[A-Za-z0-9_-]{20,}$/);
  await linkDialog.getByRole("button", { name: "Tamam" }).click();
  await expect(page.getByRole("table", { name: "Bekleyen davetler" })).toContainText(invitee.email);

  // Invitee: a separate browser profile, signed out.
  const context = await browser.newContext({ locale: "tr-TR", timezoneId: "Europe/Istanbul", viewport: { width: 1280, height: 800 } });
  const guest = await context.newPage();
  logApiErrors(guest);
  const token = link.split("#")[1];
  const urls: string[] = [];
  guest.on("request", (r) => urls.push(r.url()));

  await guest.goto(link);
  await expect(guest.getByRole("heading", { level: 1, name: "Ekibe davet edildiniz" })).toBeVisible();
  await expect(guest).toHaveURL(/\/davet$/);
  await guest.getByRole("link", { name: "Hesap oluştur" }).click();
  await expect(guest).toHaveURL(/\/kayit\?next=%2Fdavet$/);
  await guest.getByLabel("Ad soyad").fill(invitee.fullName);
  await guest.getByLabel("E-posta").fill(invitee.email);
  await guest.locator("input[name=password]").fill(invitee.password);
  await guest.locator("input[name=confirm]").fill(invitee.password);
  await guest.getByRole("button", { name: "Hesap oluştur" }).click();
  await expect(guest.getByText("daveti kabul edebilirsiniz")).toBeVisible();

  // Confirmation link (Mailpit) in the same tab: Supabase /verify → /auth/callback?code=… → /davet.
  await guest.goto(await confirmationLink(invitee.email));
  await expect(guest).toHaveURL(/\/davet$/, { timeout: 15_000 });
  await expect(guest.getByText(invitee.email)).toBeVisible();
  const accepted = guest.waitForResponse((r) => r.url().endsWith("/api/v1/invitations/accept"));
  await guest.getByRole("button", { name: "Daveti kabul et" }).click();
  const acceptResponse = await accepted;
  expect(acceptResponse.status()).toBe(200);
  expect(((await acceptResponse.json()) as { role: string; tenantSlug: string })).toMatchObject({ role: "sales", tenantSlug: owner.slug });

  // The joined business is active with the invited role.
  await expect(guest).toHaveURL(/\/pano$/, { timeout: 15_000 });
  await expect(guest.getByTestId("session-user")).toHaveText(invitee.fullName);
  await expect(guest.getByText(owner.businessName).first()).toBeVisible();
  await expect(guest.getByText("Satış", { exact: true }).first()).toBeVisible();
  // The token never travelled in a URL, and is gone from the tab.
  expect(urls.filter((u) => u.includes(token))).toEqual([]);
  expect(await guest.evaluate(() => window.sessionStorage.getItem("bayios.invitationToken"))).toBeNull();

  // Owner sees the new member; the invitation is no longer pending.
  await page.reload();
  const newRow = page.getByRole("table", { name: "Ekip üyeleri" }).getByRole("row").filter({ hasText: invitee.email });
  await expect(newRow).toContainText("Satış");
  await expect(newRow).toContainText("Aktif");
  await expect(page.getByRole("table", { name: "Bekleyen davetler" }).getByText("Bekleyen davet yok")).toBeVisible();
  await context.close();
});
