/**
 * WCAG 2.2 AA audit of every route (#54) — MOCK backend, desktop + mobile. Records every axe violation (all impact
 * levels) per route as JSON; it does not fail on violations (the CI gate is e2e/a11y.spec.ts and the feature specs).
 * It DOES fail when a route does not render its page heading, so the audit can never silently scan an error page.
 * See playwright.audit.config.ts for how to run it.
 */
import fs from "node:fs";
import path from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "../viewports";
import { TENANT_A, TENANT_B, USER, addSessionCookie, mockApi, mockSupabaseAuth, signedIn } from "../support/mock-backend";
import { createMockAssistant } from "../support/mock-assistant";
import { createMockCatalog, productId, type MockCatalog } from "../support/mock-catalog";
import { PRICE_LIST_IDS, createMockCommerce, customerId } from "../support/mock-commerce";
import { createMockFulfilment, makeLine, makeShipment, makeTask } from "../support/mock-fulfilment";
import { createMockImports, mockJob } from "../support/mock-imports";
import { createMockOrders, makeOrder } from "../support/mock-orders";
import { defaultMembers, mockTeam } from "../support/mock-members";

const WCAG_TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const OUT_DIR = path.resolve(__dirname, "..", "..", "test-results", "a11y-audit");
const INVITE_TOKEN = "TW9ja0ludml0YXRpb25Ub2tlbi1zeW50aGV0aWMtMDE";

type Ctx = ReturnType<typeof world>;

/** One tenant's worth of mock data, so every detail page has something to show. */
function world() {
  const catalog: MockCatalog = createMockCatalog({ products: 3 });
  const commerce = createMockCommerce(catalog);
  const orders = createMockOrders();
  const variant = (n: number) => catalog.products[n - 1].variants[0];
  const ctx = { catalog, commerce, orders };
  orders.orders.push(makeOrder(ctx, 1, { customerId: customerId(1), lines: [{ variantId: variant(1).id, quantity: 2 }] }));
  orders.orders.push(makeOrder(ctx, 2, { customerId: customerId(2), status: "approved", lines: [{ variantId: variant(2).id, quantity: 1 }] }));
  const fulfilment = createMockFulfilment({ userId: USER.id });
  const task = makeTask(1, { assignedTo: USER.id });
  const done = makeTask(2, { assignedTo: USER.id, status: "completed", lines: [makeLine(1, { quantityPicked: 2 }), makeLine(2, { quantityPicked: 1, quantityToPick: 1 })] });
  fulfilment.tasks.push(task, done);
  fulfilment.shipments.push(makeShipment(done, 1));
  const imports = createMockImports({ jobs: [mockJob({ status: "validated" })] });
  const assistant = createMockAssistant();
  return { ...ctx, fulfilment, imports, assistant, variant, task, shipment: fulfilment.shipments[0], job: imports.jobs[0] };
}

type RouteCase = {
  name: string;
  /** Sets up mocks and navigates; must leave the page on the route with its content loaded. */
  open: (page: Page, ctx: Ctx) => Promise<void>;
};

const visit = (route: string) => async (page: Page, ctx: Ctx) => {
  await signedIn(page, ctx);
  await page.goto(route);
};

const routes: RouteCase[] = [
  // Public (signed out)
  ...["/", "/giris", "/kayit", "/sifre-sifirla"].map((route) => ({
    name: `${route} (signed out)`,
    open: async (page: Page) => {
      await mockSupabaseAuth(page);
      await mockApi(page);
      await page.goto(route);
    },
  })),
  {
    name: "/davet (signed out, token in fragment)",
    open: async (page) => {
      await mockSupabaseAuth(page);
      await mockApi(page);
      await page.goto(`/davet#${INVITE_TOKEN}`);
    },
  },
  {
    name: "/davet (signed in)",
    open: async (page) => {
      await addSessionCookie(page.context());
      await mockSupabaseAuth(page);
      const api = await mockApi(page, { memberships: [TENANT_B] });
      await mockTeam(page, api, { joinTenant: TENANT_A });
      await page.goto(`/davet#${INVITE_TOKEN}`);
    },
  },
  { name: "/sifre-yenile", open: visit("/sifre-yenile") },
  {
    name: "/isletme-olustur",
    open: async (page) => {
      await signedIn(page, { memberships: [] });
      await page.goto("/isletme-olustur");
    },
  },
  {
    name: "/isletme-sec",
    open: async (page) => {
      await signedIn(page, { memberships: [TENANT_A, TENANT_B] });
      await page.goto("/isletme-sec");
    },
  },
  // Signed in, owner
  ...[
    "/pano",
    "/urunler",
    "/urunler/yeni",
    "/urunler/birimler",
    "/urunler/kategoriler",
    "/urunler/markalar",
    "/stok",
    "/stok/duzeltme",
    "/depo",
    "/depo/gorevler",
    "/depo/mal-kabul",
    "/depo/sevkiyatlar",
    "/fiyat-listeleri",
    "/fiyat-listeleri/sorgula",
    "/musteriler",
    "/musteriler/yeni",
    "/musteriler/gruplar",
    "/siparisler",
    "/siparisler/yeni",
    "/siparisler/whatsapp",
    "/ice-aktarma",
    "/ice-aktarma/yeni",
    "/raporlar",
    "/raporlar/siparisler",
    "/raporlar/stok-hareketleri",
    "/raporlar/kritik-stok",
    "/raporlar/toplama-gorevleri",
    "/raporlar/en-cok-siparis-edilenler",
    "/raporlar/ai-kullanimi",
    "/ayarlar",
    "/ayarlar/is-kurallari",
    "/ayarlar/ozel-alanlar",
    "/ayarlar/paket",
    "/kurulum",
    "/tasarim-sistemi",
  ].map((route) => ({ name: route, open: visit(route) })),
  {
    name: "/ayarlar/ekip",
    open: async (page, ctx) => {
      const api = await signedIn(page, ctx);
      await mockTeam(page, api, { members: defaultMembers("owner") });
      await page.goto("/ayarlar/ekip");
    },
  },
  { name: "/urunler/[id]", open: async (page, ctx) => visit(`/urunler/${productId(1)}`)(page, ctx) },
  { name: "/stok/varyant/[id]", open: async (page, ctx) => visit(`/stok/varyant/${ctx.variant(1).id}`)(page, ctx) },
  { name: "/fiyat-listeleri/[id]", open: async (page, ctx) => visit(`/fiyat-listeleri/${PRICE_LIST_IDS.bayiA}`)(page, ctx) },
  { name: "/musteriler/[id]", open: async (page, ctx) => visit(`/musteriler/${customerId(1)}`)(page, ctx) },
  { name: "/siparisler/[id] (draft)", open: async (page, ctx) => visit(`/siparisler/${ctx.orders.orders[0].id}`)(page, ctx) },
  { name: "/siparisler/[id] (approved)", open: async (page, ctx) => visit(`/siparisler/${ctx.orders.orders[1].id}`)(page, ctx) },
  { name: "/depo/gorevler/[id]", open: async (page, ctx) => visit(`/depo/gorevler/${ctx.task.id}`)(page, ctx) },
  { name: "/depo/sevkiyatlar/[id]", open: async (page, ctx) => visit(`/depo/sevkiyatlar/${ctx.shipment.id}`)(page, ctx) },
  { name: "/ice-aktarma/[id]", open: async (page, ctx) => visit(`/ice-aktarma/${ctx.job.id}`)(page, ctx) },
  {
    name: "/siparisler/whatsapp/[id]",
    open: async (page, ctx) => {
      await signedIn(page, ctx);
      await page.goto("/siparisler/whatsapp");
      await page.getByRole("textbox", { name: /^WhatsApp mesajı/ }).fill("Merhaba\n3 adet MOCK-001-1\n2 adet bilinmeyen parça");
      await page.getByRole("button", { name: "Mesajı ayrıştır" }).click();
      await expect(page).toHaveURL(/\/siparisler\/whatsapp\/[0-9a-f-]{36}$/);
    },
  },
  {
    name: "/pano (dark mode)",
    open: async (page, ctx) => {
      await page.emulateMedia({ colorScheme: "dark" });
      await visit("/pano")(page, ctx);
    },
  },
  {
    name: "/pano (warehouse_staff)",
    open: async (page, ctx) => {
      await signedIn(page, { ...ctx, memberships: [{ ...TENANT_A, role: "warehouse_staff" }] });
      await page.goto("/pano");
    },
  },
];

const slug = (value: string) => value.replace(/[^a-z0-9]+/gi, "-").replace(/^-|-$/g, "") || "root";

for (const [viewportName, viewport] of [
  ["desktop", DESKTOP],
  ["mobile", MOBILE],
] as const) {
  test.describe(`WCAG 2.2 AA audit — ${viewportName}`, () => {
    test.use({ viewport });

    for (const route of routes) {
      test(route.name, async ({ page }) => {
        const ctx = world();
        await route.open(page, ctx);
        await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible({ timeout: 15_000 });
        await page.waitForLoadState("networkidle");
        // WCAG 2.x A/AA rules + axe best practices (reported separately). target-size (WCAG 2.2 SC 2.5.8) ships disabled in
        // axe-core, so the audit turns it on explicitly. (AxeBuilder.options() replaces withTags(), hence runOnly here.)
        const result = await new AxeBuilder({ page })
          .options({ runOnly: { type: "tag", values: [...WCAG_TAGS, "best-practice"] }, rules: { "target-size": { enabled: true } } })
          .analyze();
        const record = {
          route: route.name,
          viewport: viewportName,
          url: new URL(page.url()).pathname,
          heading: (await page.getByRole("heading", { level: 1 }).first().innerText()).trim(),
          axe: result.testEngine.version,
          violations: result.violations.map((v) => ({
            id: v.id,
            impact: v.impact,
            tags: v.tags.filter((t) => t.startsWith("wcag")),
            help: v.help,
            helpUrl: v.helpUrl,
            nodes: v.nodes.map((n) => ({ target: n.target.join(" "), summary: n.failureSummary?.split("\n").slice(0, 3).join(" ") })),
          })),
          incomplete: result.incomplete.map((v) => ({
            id: v.id,
            impact: v.impact,
            count: v.nodes.length,
            nodes: v.nodes.map((n) => ({ target: n.target.join(" "), summary: n.failureSummary?.split("\n").slice(0, 3).join(" ") })),
          })),
          passes: result.passes.map((v) => v.id),
          inapplicable: result.inapplicable.map((v) => v.id),
        };
        fs.mkdirSync(OUT_DIR, { recursive: true });
        fs.writeFileSync(path.join(OUT_DIR, `${viewportName}--${slug(route.name)}.json`), JSON.stringify(record, null, 2));
        await test.info().attach("axe", { body: JSON.stringify(record, null, 2), contentType: "application/json" });
      });
    }
  });
}
