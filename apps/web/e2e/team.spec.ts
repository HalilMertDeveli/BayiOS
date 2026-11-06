/**
 * Ayarlar → Ekip (#129 web): members, invitations with one-time links, role change, suspend / re-enable, ownership
 * transfer, seat usage.
 * MOCK: Supabase Auth and the BayiOS API are faked with page.route (support/mock-backend.ts + support/mock-members.ts).
 * All data and tokens are synthetic; nothing here talks to a real API.
 */
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "./viewports";
import { TENANT_A, signedIn, type ApiMockOptions } from "./support/mock-backend";
import { createMockBilling } from "./support/mock-billing";
import { defaultMembers, member, mockTeam, type MockMember } from "./support/mock-members";

test.use({ viewport: DESKTOP });

async function expectNoSeriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  const summary = blocking.map((v) => `${v.impact} ${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
  expect(summary, summary.join("\n")).toEqual([]);
}

async function openTeam(page: Page, options: ApiMockOptions & { role?: string; members?: MockMember[] } = {}) {
  const { role = "owner", members, ...rest } = options;
  const api = await signedIn(page, { memberships: [{ ...TENANT_A, role }], ...rest });
  const team = await mockTeam(page, api, { members: members ?? defaultMembers(role) });
  // Every URL the browser requests, to prove no invitation token ever travels in a URL.
  const urls: string[] = [];
  page.on("request", (r) => urls.push(r.url()));
  await page.goto("/ayarlar/ekip");
  await expect(page.getByRole("heading", { level: 1, name: "Ekip" })).toBeVisible();
  return { api, team, urls };
}

const membersTable = (page: Page) => page.getByRole("table", { name: "Ekip üyeleri" });
const invitationsTable = (page: Page) => page.getByRole("table", { name: "Bekleyen davetler" });
const row = (page: Page, text: string) => membersTable(page).getByRole("row").filter({ hasText: text });

test("sahip: üyeler, roller, durumlar ve kullanıcı sayısı; filtreler sunucuya gider", async ({ page }) => {
  const { team } = await openTeam(page);
  await expect(row(page, "Ayşe Yönetici (mock)")).toContainText("Yönetici");
  await expect(row(page, "Deniz Depocu (mock)")).toContainText("Askıda");
  await expect(row(page, "Mock Kullanıcı")).toContainText("(siz)");
  // Own row: no management actions.
  await expect(row(page, "Mock Kullanıcı").getByRole("button")).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Kullanıcı sayısı" })).toBeVisible();
  await expect(page.getByText("4 / 25")).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.getByRole("combobox", { name: "Rol" }).selectOption("sales");
  await expect(membersTable(page).getByRole("row")).toHaveCount(2);
  await expect(row(page, "Selin Satış (mock)")).toBeVisible();
  expect(team.requests.some((r) => r.path === "/api/v1/members" && r.search.includes("role=sales"))).toBe(true);
  await page.getByRole("combobox", { name: "Durum" }).selectOption("disabled");
  await expect(membersTable(page).getByText("Bu filtrelerle üye yok")).toBeVisible();
});

test("davet: bağlantı bir kez gösterilir (#token), kopyalanır; kapatınca kaybolur, bekleyenlerde görünür", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  const { team, urls } = await openTeam(page);
  await page.getByRole("button", { name: "Kişi davet et" }).click();
  const dialog = page.getByRole("dialog", { name: "Kişi davet et" });
  // Owner may invite admin and below — never owner.
  const roleOptions = await dialog.getByRole("combobox", { name: "Rol" }).locator("option").allTextContents();
  expect(roleOptions).toEqual(["Yönetici", "Operasyon müdürü", "Satış", "Depo personeli", "İzleyici"]);
  await dialog.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();
  await expect(dialog.getByText("E-posta adresinizi girin.")).toBeVisible();
  await dialog.getByRole("textbox", { name: "E-posta" }).fill("yeni.kisi@example.test");
  await dialog.getByRole("combobox", { name: "Rol" }).selectOption("warehouse_staff");
  await dialog.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();

  const linkDialog = page.getByRole("alertdialog", { name: "Davet bağlantısı hazır" });
  await expect(linkDialog).toBeVisible();
  const token = [...team.tokens.keys()][0];
  const link = linkDialog.getByRole("textbox", { name: "Davet bağlantısı" });
  await expect(link).toHaveValue(new RegExp(`^http://localhost:\\d+/davet#${token}$`));
  await expect(linkDialog).toContainText("Bu bağlantıyı kişiye siz iletin");
  await expect(linkDialog).toContainText("7 gün geçerli");
  await expect(linkDialog).toContainText("yalnızca bir kez gösterilir");
  await expectNoSeriousViolations(page);
  await linkDialog.getByRole("button", { name: "Bağlantıyı kopyala" }).click();
  await expect(linkDialog.getByRole("button", { name: "Kopyalandı" })).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain(`/davet#${token}`);

  const create = team.requests.find((r) => r.method === "POST" && r.path === "/api/v1/members/invitations");
  expect(create?.body).toEqual({ email: "yeni.kisi@example.test", role: "warehouse_staff" });
  expect(create?.headers["idempotency-key"]).toBeUndefined();

  await linkDialog.getByRole("button", { name: "Tamam" }).click();
  await expect(linkDialog).toBeHidden();
  expect(await page.content()).not.toContain(token);
  await expect(invitationsTable(page).getByRole("row").filter({ hasText: "yeni.kisi@example.test" })).toContainText("Depo personeli");
  // Storage and URLs never held the token.
  const stored = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
  expect(stored).not.toContain(token);
  expect(urls.filter((u) => u.includes(token))).toEqual([]);
});

test("409 bekleyen davet: aynı adrese yeni bağlantı önerilir; listeden yenile ve iptal et", async ({ page }) => {
  const { team } = await openTeam(page);
  const invite = async () => {
    await page.getByRole("button", { name: "Kişi davet et" }).click();
    const dialog = page.getByRole("dialog", { name: "Kişi davet et" });
    await dialog.getByRole("textbox", { name: "E-posta" }).fill("bekleyen@example.test");
    await dialog.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();
    return dialog;
  };
  await invite();
  const first = [...team.tokens.keys()][0];
  await page.getByRole("alertdialog", { name: "Davet bağlantısı hazır" }).getByRole("button", { name: "Tamam" }).click();

  const dialog = await invite();
  await expect(dialog.getByRole("alert")).toContainText("bekleyen bir davet zaten var");
  await dialog.getByRole("button", { name: "Yeni bağlantı oluştur" }).click();
  const renewed = page.getByRole("alertdialog", { name: "Yeni davet bağlantısı hazır" });
  await expect(renewed).toBeVisible();
  const second = [...team.tokens.keys()][0];
  expect(second).not.toBe(first);
  await expect(renewed.getByRole("textbox", { name: "Davet bağlantısı" })).toHaveValue(new RegExp(`/davet#${second}$`));
  await renewed.getByRole("button", { name: "Tamam" }).click();

  // Resend from the list: yet another link; the old one is gone from the mock's token table.
  const inviteRow = invitationsTable(page).getByRole("row").filter({ hasText: "bekleyen@example.test" });
  await inviteRow.getByRole("button", { name: "Yeni bağlantı: bekleyen@example.test" }).click();
  await expect(page.getByRole("alertdialog", { name: "Yeni davet bağlantısı hazır" })).toBeVisible();
  expect(team.tokens.has(second)).toBe(false);
  await page.getByRole("alertdialog", { name: "Yeni davet bağlantısı hazır" }).getByRole("button", { name: "Tamam" }).click();

  await inviteRow.getByRole("button", { name: "Daveti iptal et: bekleyen@example.test" }).click();
  const confirm = page.getByRole("alertdialog", { name: "Davet iptal edilsin mi?" });
  await confirm.getByRole("button", { name: "Daveti iptal et" }).click();
  await expect(page.getByText("Davet iptal edildi")).toBeVisible();
  await expect(invitationsTable(page).getByText("Bekleyen davet yok")).toBeVisible();
  const revoke = team.requests.find((r) => r.path.endsWith("/revoke"));
  expect(revoke?.headers["idempotency-key"]).toBeTruthy();
});

test("rol değişikliği; son sahip hatası Türkçe gösterilir", async ({ page }) => {
  const { team } = await openTeam(page);
  await row(page, "Selin Satış (mock)").getByRole("button", { name: "Rolü değiştir: Selin Satış (mock)" }).click();
  const dialog = page.getByRole("dialog", { name: "Rolü değiştir" });
  const options = await dialog.getByRole("combobox", { name: "Yeni rol" }).locator("option").allTextContents();
  expect(options).not.toContain("İşletme sahibi");
  await expect(dialog.getByRole("button", { name: "Rolü kaydet" })).toBeDisabled();
  await dialog.getByRole("combobox", { name: "Yeni rol" }).selectOption("viewer");
  await dialog.getByRole("button", { name: "Rolü kaydet" }).click();
  await expect(page.getByText("Rol güncellendi")).toBeVisible();
  await expect(row(page, "Selin Satış (mock)")).toContainText("İzleyici");
  const put = team.requests.find((r) => r.method === "PUT");
  expect(put?.body).toEqual({ role: "viewer" });
  expect(put?.headers["idempotency-key"]).toBeTruthy();

  team.failNext = { status: 409, code: "members.last_owner" };
  await row(page, "Ayşe Yönetici (mock)").getByRole("button", { name: "Rolü değiştir: Ayşe Yönetici (mock)" }).click();
  const again = page.getByRole("dialog", { name: "Rolü değiştir" });
  await again.getByRole("combobox", { name: "Yeni rol" }).selectOption("manager");
  await again.getByRole("button", { name: "Rolü kaydet" }).click();
  await expect(again.getByRole("alert")).toHaveText("İşletmenin en az bir aktif sahibi olmalı. Önce sahipliği başka bir üyeye devredin.");
});

test("askıya alma onayla; yeniden etkinleştirme", async ({ page }) => {
  await openTeam(page);
  await row(page, "Mert Müdür (mock)").getByRole("button", { name: "Askıya al: Mert Müdür (mock)" }).click();
  const confirm = page.getByRole("alertdialog", { name: "Üye askıya alınsın mı?" });
  await expect(confirm).toContainText("verileri ve geçmişi silinmez");
  await confirm.getByRole("button", { name: "Askıya al" }).click();
  await expect(page.getByText("Üye askıya alındı")).toBeVisible();
  await expect(row(page, "Mert Müdür (mock)")).toContainText("Askıda");

  await row(page, "Deniz Depocu (mock)").getByRole("button", { name: "Etkinleştir: Deniz Depocu (mock)" }).click();
  await page.getByRole("alertdialog", { name: "Üye yeniden etkinleştirilsin mi?" }).getByRole("button", { name: "Yeniden etkinleştir" }).click();
  await expect(row(page, "Deniz Depocu (mock)")).toContainText("Aktif");
});

test("sahiplik devri: güçlü onay; sonra çağıran yönetici olur ve devir seçeneği kaybolur", async ({ page }) => {
  const { team, api } = await openTeam(page);
  await row(page, "Ayşe Yönetici (mock)").getByRole("button", { name: "Sahipliği devret: Ayşe Yönetici (mock)" }).click();
  const confirm = page.getByRole("alertdialog", { name: "İşletme sahipliği devredilsin mi?" });
  const go = confirm.getByRole("button", { name: "Sahipliği devret" });
  await expect(go).toBeDisabled();
  await confirm.getByRole("textbox", { name: /Onaylamak için yazın/ }).fill("yanlis@example.test");
  await expect(go).toBeDisabled();
  await confirm.getByRole("textbox", { name: /Onaylamak için yazın/ }).fill("uye1@example.test");
  await go.click();
  await expect(page.getByText("Sahiplik devredildi")).toBeVisible();
  expect(team.requests.find((r) => r.path === "/api/v1/members/ownership-transfer")?.body).toEqual({ userId: team.members[1].userId });
  expect(api.memberships[0].role).toBe("admin");
  await expect(row(page, "Ayşe Yönetici (mock)")).toContainText("İşletme sahibi");
  // Now admin: no transfer, and the new owner cannot be managed.
  await expect(page.getByRole("button", { name: /Sahipliği devret/ })).toHaveCount(0);
  await expect(row(page, "Ayşe Yönetici (mock)").getByRole("button")).toHaveCount(0);
});

test("max_users: koltuk dolu uyarısı ve davet reddi", async ({ page }) => {
  const members = [...defaultMembers("owner"), member(5, "Ek Üye (mock)", "viewer")];
  await openTeam(page, { billing: createMockBilling({ plan: "basic" }), members });
  await expect(page.getByText("5 / 5")).toBeVisible();
  await expect(page.getByText(/Sınıra ulaştınız/)).toBeVisible();
  await page.getByRole("button", { name: "Kişi davet et" }).click();
  const dialog = page.getByRole("dialog", { name: "Kişi davet et" });
  await dialog.getByRole("textbox", { name: "E-posta" }).fill("fazla@example.test");
  await dialog.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();
  await expect(dialog.getByRole("alert")).toContainText("Kullanıcı sayısı sınırına ulaşıldı (5/5)");
});

test("yönetici: admin davet edemez, sahip ve diğer yöneticileri yönetemez", async ({ page }) => {
  const members = [...defaultMembers("admin"), { ...member(9, "Oya Sahip (mock)", "owner") }];
  await openTeam(page, { role: "admin", members });
  await page.getByRole("button", { name: "Kişi davet et" }).click();
  const options = await page.getByRole("dialog", { name: "Kişi davet et" }).getByRole("combobox", { name: "Rol" }).locator("option").allTextContents();
  expect(options).toEqual(["Operasyon müdürü", "Satış", "Depo personeli", "İzleyici"]);
  await page.getByRole("dialog", { name: "Kişi davet et" }).getByRole("button", { name: "Vazgeç" }).click();
  await expect(row(page, "Oya Sahip (mock)").getByRole("button")).toHaveCount(0);
  await expect(row(page, "Ayşe Yönetici (mock)").getByRole("button")).toHaveCount(0);
  await expect(row(page, "Mert Müdür (mock)").getByRole("button", { name: /Rolü değiştir/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Sahipliği devret/ })).toHaveCount(0);
  // admin has billing.read: the seat counter is shown.
  await expect(page.getByText("kullanıcı (aktif üyeler ve bekleyen davetler)")).toBeVisible();
});

test("müdür: ekibi görür, davet ve yönetim yok, kullanıcı sayısı gösterilmez", async ({ page }) => {
  await openTeam(page, { role: "manager" });
  await expect(row(page, "Selin Satış (mock)")).toBeVisible();
  await expect(page.getByRole("button", { name: "Kişi davet et" })).toHaveCount(0);
  await expect(membersTable(page).getByRole("button")).toHaveCount(0);
  await expect(page.getByText("kullanıcı (aktif üyeler ve bekleyen davetler)")).toHaveCount(0);
});

for (const role of ["sales", "viewer", "warehouse_staff"]) {
  test(`${role}: ekibe erişim yok, istek yok`, async ({ page }) => {
    const api = await signedIn(page, { memberships: [{ ...TENANT_A, role }] });
    const team = await mockTeam(page, api);
    await page.goto("/ayarlar/ekip");
    await expect(page.getByText("Ekibi görüntüleme yetkiniz yok")).toBeVisible();
    expect(team.requests).toHaveLength(0);
  });
}

test("ayarlar sayfası ekip ve iş kuralları bağlantılarını gösterir", async ({ page }) => {
  await signedIn(page);
  await page.goto("/ayarlar");
  await page.getByRole("link", { name: /Ekip/ }).click();
  await expect(page).toHaveURL(/\/ayarlar\/ekip$/);
  await page.goto("/ayarlar");
  await expect(page.getByRole("link", { name: /İş kuralları/ })).toBeVisible();
});

test("sayfalama: 50'den fazla üye 'Daha fazla yükle' ile gelir", async ({ page }) => {
  const many = [...defaultMembers("owner"), ...Array.from({ length: 56 }, (_, i) => member(100 + i, `Toplu Üye ${String(i + 1).padStart(2, "0")} (mock)`, "viewer"))];
  const { team } = await openTeam(page, { members: many });
  await expect(membersTable(page).getByRole("row")).toHaveCount(51);
  await page.getByRole("button", { name: "Daha fazla yükle" }).click();
  await expect(membersTable(page).getByRole("row")).toHaveCount(62);
  await expect(page.getByRole("button", { name: "Daha fazla yükle" })).toHaveCount(0);
  expect(team.requests.some((r) => r.search.includes("cursor=50"))).toBe(true);
});

test("mobil: ekip ve davet bağlantısı telefona sığar, erişilebilir", async ({ page }) => {
  await page.setViewportSize(MOBILE);
  await openTeam(page);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await expectNoSeriousViolations(page);
  await page.getByRole("button", { name: "Kişi davet et" }).click();
  const dialog = page.getByRole("dialog", { name: "Kişi davet et" });
  await dialog.getByRole("textbox", { name: "E-posta" }).fill("mobil@example.test");
  await dialog.getByRole("button", { name: "Davet bağlantısı oluştur" }).click();
  await expect(page.getByRole("alertdialog", { name: "Davet bağlantısı hazır" })).toBeVisible();
  await expectNoSeriousViolations(page);
});
