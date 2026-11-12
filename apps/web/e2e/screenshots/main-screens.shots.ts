/**
 * Screenshots of the main screens for UI review (desktop 1280 / mobile 390, light / dark) — MOCK backend only.
 * All data is synthetic (support/*). Run with playwright.shots.config.ts; SHOTS_DIR picks the output folder
 * (relative to apps/web), e.g. docs/screenshots/ui-polish/after.
 */
import fs from "node:fs";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { DESKTOP, MOBILE } from "../viewports";
import { USER, mockApi, mockSupabaseAuth, signedIn } from "../support/mock-backend";
import { createMockAssistant, parseId } from "../support/mock-assistant";
import { createMockCatalog, productId } from "../support/mock-catalog";
import { createMockCommerce, customerId } from "../support/mock-commerce";
import { createMockFulfilment, makeLine, makeShipment, makeTask } from "../support/mock-fulfilment";
import { LOCATION_IDS, WAREHOUSE_IDS, createMockInventory } from "../support/mock-inventory";
import { createMockOrders, makeOrder } from "../support/mock-orders";
import { defaultMembers, mockTeam } from "../support/mock-members";
import { createMockStockDocuments, type MockCount, type MockTransfer } from "../support/mock-stock-documents";

const webRoot = path.resolve(__dirname, "..", "..");
const OUT_DIR = path.resolve(webRoot, process.env.SHOTS_DIR ?? "test-results/shots");
const FORMAT = (process.env.SHOTS_FORMAT ?? "jpeg") as "jpeg" | "png";

const docUuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;
const T0 = "2026-11-08T07:30:00Z";

function world() {
  const catalog = createMockCatalog({ products: 8 });
  const commerce = createMockCommerce(catalog);
  const inventory = createMockInventory(catalog, { stocked: 8 });
  // A low and an empty balance, so stock tables show every state.
  inventory.balances[5].onHand = 1;
  inventory.balances[5].reserved = 1;
  inventory.balances[5].available = 0;
  inventory.balances[6].onHand = 0;
  inventory.balances[6].reserved = 0;
  inventory.balances[6].available = 0;
  const orders = createMockOrders();
  const variant = (n: number) => catalog.products[n - 1].variants[0];
  const ctx = { catalog, commerce };
  const statuses = ["pending_approval", "pending_approval", "approved", "picking", "packed", "shipped", "completed", "draft", "cancelled"];
  statuses.forEach((status, i) => {
    orders.orders.push(
      makeOrder(ctx, i + 1, {
        customerId: customerId((i % 3) + 1),
        status,
        lines: [
          { variantId: variant((i % 8) + 1).id, quantity: 2 + i },
          { variantId: variant(((i + 3) % 8) + 1).id, quantity: 1 },
        ],
      }),
    );
  });
  const fulfilment = createMockFulfilment({ userId: USER.id });
  const task = makeTask(1, { assignedTo: USER.id, status: "in_progress", priority: "urgent" });
  const pool = makeTask(3);
  const done = makeTask(2, { assignedTo: USER.id, status: "completed", lines: [makeLine(1, { quantityPicked: 2 }), makeLine(2, { quantityPicked: 1, quantityToPick: 1 })] });
  fulfilment.tasks.push(task, pool, done);
  fulfilment.shipments.push(makeShipment(done, 1));
  const assistant = createMockAssistant({
    parses: [
      {
        id: parseId(1),
        status: "needs_review",
        parser: "rules",
        fallbackReason: "disabled",
        summary: { lines: 3, matched: 2, ambiguous: 1, unmatched: 0, overallConfidence: 0.8 },
        customerId: null,
        orderId: null,
        createdAt: "2026-11-08T08:30:00Z",
        createdBy: USER.id,
      },
    ],
  });
  const transfer: MockTransfer = {
    id: docUuid("5e", 1),
    number: "TRF-00000001",
    sourceWarehouseId: WAREHOUSE_IDS.main,
    destinationWarehouseId: WAREHOUSE_IDS.raf,
    status: "in_transit",
    note: null,
    lines: [
      { id: docUuid("5f", 1), lineNo: 1, variantId: variant(1).id, sku: variant(1).sku, sourceLocationId: null, destinationLocationId: LOCATION_IDS.a01, quantity: 4, receivedQuantity: 0, returnedQuantity: 0, lostQuantity: 0 },
      { id: docUuid("5f", 2), lineNo: 2, variantId: variant(2).id, sku: variant(2).sku, sourceLocationId: null, destinationLocationId: LOCATION_IDS.a02, quantity: 2, receivedQuantity: 0, returnedQuantity: 0, lostQuantity: 0 },
    ],
    isOverdue: false,
    dispatchedAt: T0,
    lastReceivedAt: null,
    completedAt: null,
    createdAt: T0,
    closeResolution: null,
    closeReason: null,
    cancelledAt: null,
    cancelReason: null,
    version: 2,
  };
  const count: MockCount = {
    id: docUuid("6e", 1),
    number: "SYM-00000001",
    warehouseId: WAREHOUSE_IDS.main,
    scope: "warehouse",
    locationIds: [],
    variantIds: [],
    isBlind: false,
    status: "open",
    note: null,
    lines: [1, 2, 3].map((n) => ({
      id: docUuid("6f", n),
      variantId: variant(n).id,
      sku: variant(n).sku,
      locationId: null,
      expectedQuantity: 10 + n,
      countedQuantity: n === 1 ? 11 : n === 2 ? 10 : null,
      snapshotAt: T0,
      countedAt: n < 3 ? T0 : null,
      status: n < 3 ? "counted" : "uncounted",
      movedSinceSnapshot: false,
      appliedDelta: null,
    })),
    snapshotAt: T0,
    submittedAt: null,
    appliedAt: null,
    createdAt: T0,
    cancelledAt: null,
    cancelReason: null,
    version: 1,
  };
  const stockDocuments = createMockStockDocuments({ transfers: [transfer], counts: [count] });
  return { catalog, commerce, inventory, orders, fulfilment, assistant, stockDocuments, variant, task, shipment: fulfilment.shipments[0] };
}

type Ctx = ReturnType<typeof world>;
type Shot = { name: string; open: (page: Page, ctx: Ctx) => Promise<void> };

const visit = (route: string) => async (page: Page, ctx: Ctx) => {
  await signedIn(page, ctx);
  await page.goto(route);
};

const shots: Shot[] = [
  ...[
    ["giris", "/giris"],
    ["kayit", "/kayit"],
  ].map(([name, route]) => ({
    name,
    open: async (page: Page) => {
      await mockSupabaseAuth(page);
      await mockApi(page);
      await page.goto(route);
    },
  })),
  { name: "kurulum", open: visit("/kurulum") },
  { name: "pano", open: visit("/pano") },
  { name: "urunler", open: visit("/urunler") },
  { name: "urun-detay", open: async (page, ctx) => visit(`/urunler/${productId(1)}`)(page, ctx) },
  { name: "musteriler", open: visit("/musteriler") },
  { name: "fiyat-listeleri", open: visit("/fiyat-listeleri") },
  { name: "stok", open: visit("/stok") },
  { name: "stok-varyant", open: async (page, ctx) => visit(`/stok/varyant/${ctx.variant(1).id}`)(page, ctx) },
  { name: "depolar", open: visit("/depo") },
  { name: "mal-kabul", open: visit("/depo/mal-kabul") },
  { name: "transferler", open: visit("/depo/transferler") },
  { name: "transfer-detay", open: async (page, ctx) => visit(`/depo/transferler/${ctx.stockDocuments.transfers[0].id}`)(page, ctx) },
  { name: "sayimlar", open: visit("/depo/sayimlar") },
  { name: "sayim-detay", open: async (page, ctx) => visit(`/depo/sayimlar/${ctx.stockDocuments.counts[0].id}`)(page, ctx) },
  { name: "siparisler", open: visit("/siparisler") },
  { name: "siparis-detay", open: async (page, ctx) => visit(`/siparisler/${ctx.orders.orders[0].id}`)(page, ctx) },
  { name: "siparis-yeni", open: visit("/siparisler/yeni") },
  {
    name: "whatsapp-inceleme",
    open: async (page, ctx) => {
      await signedIn(page, ctx);
      await page.goto("/siparisler/whatsapp");
      await page.getByRole("textbox", { name: /^WhatsApp mesajı/ }).fill("Merhaba\n3 adet MOCK-001-1\n2 adet bilinmeyen parça");
      await page.getByRole("button", { name: "Mesajı ayrıştır" }).click();
      await expect(page).toHaveURL(/\/siparisler\/whatsapp\/[0-9a-f-]{36}$/);
    },
  },
  { name: "gorevler", open: visit("/depo/gorevler") },
  { name: "toplama", open: async (page, ctx) => visit(`/depo/gorevler/${ctx.task.id}`)(page, ctx) },
  { name: "sevkiyatlar", open: visit("/depo/sevkiyatlar") },
  { name: "raporlar", open: visit("/raporlar") },
  { name: "rapor-siparisler", open: visit("/raporlar/siparisler") },
  { name: "ayarlar", open: visit("/ayarlar") },
  {
    name: "ekip",
    open: async (page, ctx) => {
      const api = await signedIn(page, ctx);
      await mockTeam(page, api, { members: defaultMembers("owner") });
      await page.goto("/ayarlar/ekip");
    },
  },
];

const ONLY = process.env.SHOTS_ONLY?.split(",").filter(Boolean);

/**
 * SHOTS_VIEWPORTS picks the sizes (comma list, default "desktop,mobile"). "kit" = 1440×1024, the UI kit reference
 * width (#222); "tablet" = 1024×768 (the 768–1024 responsive range).
 */
const VIEWPORTS = {
  desktop: DESKTOP,
  mobile: MOBILE,
  kit: { width: 1440, height: 1024 },
  tablet: { width: 1024, height: 768 },
} as const;
const WANTED = (process.env.SHOTS_VIEWPORTS ?? "desktop,mobile").split(",").filter((v): v is keyof typeof VIEWPORTS => v in VIEWPORTS);
const SCHEMES = (process.env.SHOTS_SCHEMES ?? "light,dark").split(",").filter((v): v is "light" | "dark" => v === "light" || v === "dark");

for (const [viewportName, viewport] of WANTED.map((name) => [name, VIEWPORTS[name]] as const)) {
  for (const scheme of SCHEMES) {
    test.describe(`${viewportName} ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });
      for (const shot of shots) {
        if (ONLY && !ONLY.includes(shot.name)) continue;
        test(shot.name, async ({ page }) => {
          await shot.open(page, world());
          await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible({ timeout: 15_000 });
          await page.waitForLoadState("networkidle");
          // Let skeletons settle and fonts load.
          await page.evaluate(() => document.fonts.ready);
          await page.waitForTimeout(300);
          fs.mkdirSync(OUT_DIR, { recursive: true });
          await page.screenshot({
            path: path.join(OUT_DIR, `${viewportName}-${scheme}-${shot.name}.${FORMAT === "jpeg" ? "jpg" : "png"}`),
            fullPage: true,
            type: FORMAT,
            ...(FORMAT === "jpeg" ? { quality: 80 } : {}),
            animations: "disabled",
          });
        });
      }
    });
  }
}
