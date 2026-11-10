import { randomBytes } from "node:crypto";
import { expect, test } from "@playwright/test";
import { seedDemo } from "./demo/seeder.mjs";
import { stackConfig } from "./demo/stack.mjs";
import {
  escapeRegExp,
  expectStock,
  feedback,
  openPickTask,
  orderNumberOnPage,
  packAndShip,
  packUnits,
  pickUnits,
  scanShelf,
  signIn,
  submitAndApprove,
  tryMoney,
} from "./support/demo";
import { logApiErrors } from "./support/real-stack";

/**
 * PILOT DEMO #56 — "Demo Motor Yedek Parça" on the REAL stack (Supabase Auth + API + PostgreSQL 17 + production web
 * build; AI parsing off → deterministic rules parser). A small version of the synthetic demo is seeded through the
 * public API as its owner (apps/web/e2e-local/demo), then the business story runs in the web UI:
 * a dealer (Bayi A) sends a WhatsApp message for a set of brake parts → review (one line by name, chosen by hand) →
 * draft with dealer prices → submit + approve (reservation) → pick by shelf + barcode (a wrong item is rejected) →
 * pack check → shipment confirmed → stock left the shelf (ledger).
 */

test("motor demo: bayi WhatsApp'tan fren seti → inceleme → onay → barkodla toplama → sevk", async ({ page }) => {
  test.setTimeout(300_000);
  const stamp = `${Date.now().toString(36)}${randomBytes(2).toString("hex")}`; // unique tenant/user per run
  logApiErrors(page);
  const owner = { email: `e2e-demo-motor-${stamp}@example.test`, password: `E2e-Demo-Motor-${randomBytes(9).toString("base64url")}`, fullName: "E2E Demo Motor Sahibi" };
  const demo = await seedDemo({
    cfg: stackConfig(),
    demo: "motor",
    size: "small",
    owner,
    tenant: { name: `E2E Demo Motor ${stamp}`, slug: `e2e-demo-motor-${stamp}` },
    include: { importCatalog: false, orders: true, whatsapp: false },
    log: (line) => console.log(`[seed] ${line}`),
  });
  console.log(`[seed] small motor demo in ${demo.seconds.toFixed(1)} s: ${JSON.stringify(demo.counts)}`);
  expect(demo.counts.products).toBe(16);
  expect(demo.counts.ordersByStatus).toMatchObject({ approved: 1, shipped: 1 });

  const dealer = demo.customers[0]; // "Usta Ali Motor (sentetik)", dealer group Bayi A
  expect(dealer.group).toBe("Bayi A");
  const intents = demo.def.whatsapp.lines;
  const main = demo.warehouses.MERKEZ;

  await signIn(page, owner.email, owner.password, owner.fullName);

  // ---- 1. WhatsApp message from the dealer, pasted on Siparişler → WhatsApp siparişi.
  await page.goto("/siparisler");
  await page.getByRole("link", { name: "WhatsApp siparişi" }).click();
  // AI parsing is opt-in per tenant (data-policy §7, off by default): switch it on when the demo tenant has it off.
  const optIn = page.getByRole("button", { name: "AI ayrıştırmayı aç" });
  const customerSearch = page.getByRole("searchbox", { name: "Müşteri (isteğe bağlı)" });
  await expect(optIn.or(customerSearch)).toBeVisible();
  if (await optIn.isVisible()) await optIn.click();
  await customerSearch.fill(dealer.code);
  await page.getByRole("button", { name: "Müşteri ara" }).click();
  await page.getByRole("button", { name: new RegExp(escapeRegExp(dealer.name)) }).click();
  await page.getByRole("textbox", { name: /^WhatsApp mesajı/ }).fill(demo.def.whatsapp.text);
  const submitted = page.waitForResponse((r) => r.url().endsWith("/api/v1/ai/order-parses") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Mesajı ayrıştır" }).click();
  // 202: accepted; the page polls the status until the background parse is done, then opens the review.
  expect((await submitted).status()).toBe(202);

  // ---- 2. Review: SKU lines are proposed (confirm each), the name-only line is chosen by hand; nothing is priced here.
  await expect(page).toHaveURL(/\/siparisler\/whatsapp\/[0-9a-f-]{36}$/);
  await expect(page.getByText("Ayrıştırıcı: Kural tabanlı")).toBeVisible();
  const regions = page.getByRole("region", { name: /^Satır \d+$/ });
  await expect(regions.first()).toBeVisible();
  // (The previous route stays in the DOM hidden, so the check is scoped to the review lines.)
  await expect(regions.filter({ hasText: "₺" }), "no prices on the review screen").toHaveCount(0);
  // #133: the card shows the catalog unit's display name ("Takım"), never its code ("takim").
  const unitNames = new Map((await demo.api.get<{ items: { code: string; name: string }[] }>("/api/v1/units")).items.map((u) => [u.code, u.name]));
  expect(unitNames.get("takim")).toBe("Takım");
  const handled = new Set<string>();
  for (const intent of intents) {
    const region = regions.filter({ hasText: intent.raw });
    await expect(region, `review line for "${intent.raw}"`).toHaveCount(1);
    const name = (await region.getByRole("heading", { level: 3 }).innerText()).trim();
    handled.add(name);
    const byCode = intent.raw.includes(intent.sku);
    if (byCode) {
      await expect(region.getByText("Eşleşti", { exact: true })).toBeVisible();
      await expect(region).toContainText(intent.sku);
      const unit = demo.variants[intent.sku].unit;
      await expect(region.getByText(`Birim: ${unitNames.get(unit)}`, { exact: true })).toBeVisible();
      await region.getByLabel(`${name}: önerilen ürünü onaylıyorum`, { exact: true }).check();
    } else {
      // "cg 125 arka balata": original and aftermarket pads both fit → the reviewer picks the dealer's usual Muadil.
      const other = region.getByRole("button", { name: "Başka ürün seç" });
      if (await other.isVisible()) await other.click();
      const search = region.getByRole("textbox", { name: `${name}: katalogda ara` });
      await search.fill(intent.sku);
      await search.press("Enter");
      await expect(region.getByRole("button", { name: "Başka ürün seç" })).toBeVisible();
      await expect(region).toContainText(intent.sku);
    }
    await page.getByLabel(`${name} miktarı`, { exact: true }).fill(String(intent.quantity));
  }
  // Anything else the parser made a line of is not part of this order.
  for (const name of (await regions.getByRole("heading", { level: 3 }).allInnerTexts()).map((t) => t.trim())) {
    if (!handled.has(name)) await page.getByRole("button", { name: `${name}: siparişten çıkar` }).click();
  }
  const confirm = page.getByRole("button", { name: "Taslak siparişi oluştur" });
  await expect(confirm).toBeEnabled();
  const approved = page.waitForResponse((r) => r.url().endsWith("/approve") && r.request().method() === "POST");
  await confirm.click();
  const approveResponse = await approved;
  expect(approveResponse.status()).toBe(200);
  const orderId = ((await approveResponse.json()) as { orderId: string }).orderId;

  // ---- 3. The draft: source WhatsApp mesajı, dealer prices from the Bayi A list (resolved by the API, not the message).
  await expect(page).toHaveURL(new RegExp(`/siparisler/${orderId}$`));
  await expect(page.getByRole("definition").filter({ hasText: "WhatsApp mesajı" })).toBeVisible();
  const orderNumber = await orderNumberOnPage(page);
  type OrderDetail = {
    order: { source: string; status: string; totals: { grandTotal: number | string } | null };
    lines: { sku: string; quantity: number | string; unitPrice: number | string | null; priceListId: string | null; variantId: string }[];
  };
  const draft = await demo.api.get<OrderDetail>(`/api/v1/orders/${orderId}`);
  expect(draft.order).toMatchObject({ source: "whatsapp_ai", status: "draft" });
  expect(Object.fromEntries(draft.lines.map((l) => [l.sku, Number(l.quantity)]))).toEqual(Object.fromEntries(intents.map((i) => [i.sku, i.quantity])));
  const dealerList = demo.priceLists.dealer["Bayi A"].id;
  const quote = await demo.api.post<{ lines: { variantId: string; unitPrice: number | string | null; priceListId: string | null }[] }>("/api/v1/pricing/quote", {
    customerId: dealer.id,
    date: null,
    currency: "TRY",
    lines: draft.lines.map((l) => ({ variantId: l.variantId, quantity: l.quantity })),
  });
  for (const line of draft.lines) {
    expect(line.priceListId, `${line.sku} priced from the dealer list`).toBe(dealerList);
    const quoted = quote.lines.find((q) => q.variantId === line.variantId)!;
    expect(Number(line.unitPrice)).toBe(Number(quoted.unitPrice));
    expect(Number(line.unitPrice)).toBeLessThan(demo.variants[line.sku].price); // 12 % below retail
  }
  await expect(page.getByTestId("order-grand-total")).toHaveText(tryMoney(draft.order.totals!.grandTotal));

  // ---- 4. Submit + approve: stock reserved on the shelves.
  await submitAndApprove(page, orderNumber);
  const pads = intents[0];
  const padStock = demo.stock[pads.sku].main;
  await expectStock(page, main.id, pads.sku, padStock, pads.quantity, padStock - pads.quantity);

  // ---- 5. Picking by shelf + barcode. A wrong item (the Orijinal pads) is rejected on the brake shelf.
  await openPickTask(page, orderNumber);
  const brakeShelf = main.locations["Fren"].code;
  await expect(page.getByTestId("pick-step")).toHaveText(`1. Rafı okutun: ${brakeShelf}`);
  await scanShelf(page, brakeShelf);
  const wrong = demo.variants[pads.sku.replace(/-MU$/, "-OR")];
  await page.getByRole("textbox", { name: /Raf barkodu|Ürün barkodu veya SKU/ }).fill(wrong.barcode!);
  await page.getByRole("textbox", { name: /Raf barkodu|Ürün barkodu veya SKU/ }).press("Enter");
  await expect(feedback(page)).toContainText("Yanlış ürün");
  for (const intent of intents) {
    const variant = demo.variants[intent.sku];
    await scanShelf(page, main.locations[variant.category].code);
    await pickUnits(page, intent.sku, variant.barcode!, intent.quantity);
  }
  await expect(feedback(page)).toContainText("Tüm satırlar bitti");
  await page.getByRole("button", { name: "Görevi tamamla" }).click();
  await expect(page.getByText("Toplama tamamlandı", { exact: true })).toBeVisible();

  // ---- 6. Pack check + shipment.
  await page.getByRole("link", { name: "Sevkiyata git" }).click();
  await expect(page).toHaveURL(/\/depo\/sevkiyatlar\/[0-9a-f-]{36}$/);
  for (const intent of intents) await packUnits(page, intent.sku, demo.variants[intent.sku].barcode!, intent.quantity);
  await packAndShip(page, "Demo Kargo (sentetik)", `DEMO-E2E-${stamp}`);

  // ---- 7. Order shipped; stock left the shelf and the reservation was consumed (ledger: shipment_out).
  await page.goto(`/siparisler/${orderId}`);
  await expect(page.getByText("Sevk Edildi").first()).toBeVisible();
  await expect(page.getByText("Tamamı sevk edildi").first()).toBeVisible();
  await expectStock(page, main.id, pads.sku, padStock - pads.quantity, 0, padStock - pads.quantity);
  const shipped = await demo.api.get<{ order: { status: string; fulfillmentStatus: string } }>(`/api/v1/orders/${orderId}`);
  expect(shipped.order).toMatchObject({ status: "shipped", fulfillmentStatus: "full" });
  await page.screenshot({ path: "test-results/demo-motor-shipped.png", fullPage: true });
});
