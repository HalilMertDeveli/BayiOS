import { readFile } from "node:fs/promises";
import { randomBytes } from "node:crypto";
import { expect, test } from "@playwright/test";
import { runCatalogImport, seedDemo } from "./demo/seeder.mjs";
import { stackConfig } from "./demo/stack.mjs";
import {
  createDraftOrder,
  expectStock,
  feedback,
  openPickTask,
  packAndShip,
  packUnits,
  pickBox,
  scanShelf,
  signIn,
  submitAndApprove,
  tryMoney,
} from "./support/demo";
import { logApiErrors } from "./support/real-stack";

/**
 * PILOT DEMO #57 — "Demo LED Elektronik" on the REAL stack (nothing mocked). A small version of the synthetic demo is
 * seeded through the public API as its owner, then: the supplier catalog (generated XLSX) is bulk-imported through
 * the Imports API (the web has no import screen yet — /ice-aktarma is a placeholder) and shows up in the catalog →
 * a quantity tier (20+) is added on the default price list in the UI → goods receipt by barcode onto shelves → an
 * order of 25 gets the tier price → picking with a damaged-goods shortage → partial shipment → short-close of the rest
 * on the order detail ("Kalanı kapat") → stock and reservations from the ledger → top-products report exported as CSV and XLSX.
 */
const PSU = "PSU-5V40A";
const CABLE = "KBL-FLAT-16P";
const TIER = { minQuantity: 20, unitPrice: 590 };
const RECEIVED = { [PSU]: 40, [CABLE]: 60 };
const ORDERED = { [PSU]: 25, [CABLE]: 12 };
const DAMAGED = 3;

test("LED demo: toplu katalog içe aktarma → fiyat kademesi → kademeli fiyatlı sipariş → kısmi sevk + kalanı kapatma → rapor", async ({ page }) => {
  test.setTimeout(300_000);
  const stamp = `${Date.now().toString(36)}${randomBytes(2).toString("hex")}`; // unique tenant/user per run
  logApiErrors(page);
  const owner = { email: `e2e-demo-led-${stamp}@example.test`, password: `E2e-Demo-Led-${randomBytes(9).toString("base64url")}`, fullName: "E2E Demo LED Sahibi" };
  const demo = await seedDemo({
    cfg: stackConfig(),
    demo: "led",
    size: "small",
    owner,
    tenant: { name: `E2E Demo LED ${stamp}`, slug: `e2e-demo-led-${stamp}` },
    include: { importCatalog: false, orders: true, whatsapp: false },
    log: (line) => console.log(`[seed] ${line}`),
  });
  console.log(`[seed] small LED demo in ${demo.seconds.toFixed(1)} s: ${JSON.stringify(demo.counts)}`);
  expect(demo.counts.ordersByStatus).toMatchObject({ approved: 1, shipped: 1 });
  const ist = demo.warehouses.IST;
  const customer = demo.customers.find((c) => c.group === "Kurulum")!; // no dealer list → the default list applies

  await signIn(page, owner.email, owner.password, owner.fullName);

  // ---- 1. Bulk catalog import: supplier XLSX (15 products with prices and technical attributes).
  const imported = await runCatalogImport(demo.api, demo.def, demo.priceLists.default, (line) => console.log(`[import] ${line}`));
  expect(imported.job.status).toBe("completed");
  expect(imported.job.progress).toMatchObject({ created: demo.def.importProducts.length, failed: 0 });
  await page.goto(`/urunler?q=${encodeURIComponent("PSU-")}`);
  await expect(page.getByRole("heading", { level: 1, name: "Ürünler" })).toBeVisible();
  for (const sku of demo.def.importProducts.filter((p) => p.sku.startsWith("PSU-")).map((p) => p.sku)) {
    await expect(page.getByRole("table").getByText(sku, { exact: true })).toBeVisible();
  }

  // ---- 2. Quantity tier on the default list (UI): 20+ units of the PSU cost less.
  await page.goto(`/fiyat-listeleri/${demo.priceLists.default}`);
  await expect(page.getByRole("heading", { level: 1, name: demo.def.priceLists.defaultName })).toBeVisible();
  const listTable = page.getByRole("table", { name: `${demo.def.priceLists.defaultName} fiyatları` });
  const listPrice = demo.def.importProducts.find((p) => p.sku === PSU)!.price;
  await expect(listTable).toContainText(tryMoney(listPrice)); // imported list price, tier 1
  const priceScanner = page.getByRole("textbox", { name: "Ürün okut veya ara" });
  await priceScanner.fill(PSU);
  await priceScanner.press("Enter");
  await expect(page.getByText(`${PSU} eklendi; fiyatını girin.`)).toBeVisible();
  await page.getByLabel("1. satır en az miktar").fill(String(TIER.minQuantity));
  await page.getByLabel("1. satır birim fiyat").fill(String(TIER.unitPrice));
  await page.getByRole("button", { name: "1 satırı kaydet" }).click();
  await expect(page.getByText("1 yeni, 0 güncellenen kalem.")).toBeVisible();
  await expect(listTable).toContainText(tryMoney(TIER.unitPrice));

  // ---- 3. Goods receipt in the UI: both imported items scanned onto their shelves in İstanbul Merkez.
  await page.goto("/depo/mal-kabul");
  await expect(page.getByRole("heading", { level: 1, name: "Mal kabul" })).toBeVisible();
  await page.getByRole("combobox", { name: "Depo", exact: true }).selectOption({ label: `${ist.name} (IST) — varsayılan` });
  const receiptScanner = page.getByLabel("Barkod veya SKU okutun");
  for (const [sku, category] of [[PSU, "Güç Kaynağı"], [CABLE, "Kablo & Konnektör"]] as const) {
    await receiptScanner.fill(imported.variants[sku].barcode!);
    await receiptScanner.press("Enter");
    await expect(page.getByRole("status").filter({ hasText: `${sku} eklendi.` })).toBeVisible();
    await page.getByLabel(new RegExp(`^${sku}( — .*)? miktarı$`)).fill(String(RECEIVED[sku]));
    await page.getByLabel(`${sku} rafı`).selectOption({ label: ist.locations[category].code });
  }
  await page.getByLabel("Referans (irsaliye / fatura no)").fill(`IRS-E2E-LED-${stamp}`);
  await page.getByRole("button", { name: "Mal kabulü kaydet" }).click();
  await expect(page.getByText(`2 satır, toplam ${RECEIVED[PSU] + RECEIVED[CABLE]} birim stoğa girdi.`)).toBeVisible();

  // ---- 4. Order of 25 PSUs → the 20+ tier price; cable at list price per metre.
  const { orderId, orderNumber } = await createDraftOrder(page, customer, [
    { sku: PSU, code: imported.variants[PSU].barcode!, quantity: ORDERED[PSU] },
    { sku: CABLE, code: imported.variants[CABLE].barcode!, quantity: ORDERED[CABLE] },
  ]);
  const draftForm = page.getByRole("form", { name: "Taslak sipariş" });
  await expect(draftForm.getByTestId("draft-line-0")).toContainText(tryMoney(TIER.unitPrice));
  await expect(draftForm.getByTestId("draft-line-1")).toContainText(tryMoney(demo.def.importProducts.find((p) => p.sku === CABLE)!.price));
  type Order = { order: { status: string; fulfillmentStatus: string; totals: { grandTotal: number | string } | null }; lines: { sku: string; unitPrice: number | string | null; cancelledQuantity: number | string; shippedQuantity: number | string }[] };
  const draft = await demo.api.get<Order>(`/api/v1/orders/${orderId}`);
  expect(Number(draft.lines.find((l) => l.sku === PSU)!.unitPrice)).toBe(TIER.unitPrice);
  await expect(page.getByTestId("order-grand-total")).toHaveText(tryMoney(draft.order.totals!.grandTotal));
  await submitAndApprove(page, orderNumber);
  await expectStock(page, ist.id, PSU, RECEIVED[PSU], ORDERED[PSU], RECEIVED[PSU] - ORDERED[PSU]);

  // ---- 5. Picking: 22 good PSUs (scan with a step of 22), 3 reported damaged; the full cable length.
  await openPickTask(page, orderNumber);
  const pickWith = async (sku: string, code: string, quantity: number) => {
    for (let step = 1; step < quantity; step++) await page.getByRole("button", { name: "Adet artır" }).click();
    await expect(page.getByTestId("step-value")).toHaveText(String(quantity));
    await pickBox(page).fill(code);
    await pickBox(page).press("Enter");
    await expect(feedback(page)).toContainText(`${sku} +${quantity}`);
  };
  await scanShelf(page, ist.locations["Güç Kaynağı"].code);
  await pickWith(PSU, imported.variants[PSU].barcode!, ORDERED[PSU] - DAMAGED);
  await page.getByRole("button", { name: "Eksik bildir" }).click();
  const shortage = page.getByRole("dialog", { name: `${PSU} için eksik bildir` });
  await shortage.getByLabel("Sebep").selectOption("damaged");
  await expect(shortage.getByLabel(/^Eksik miktar/)).toHaveValue(String(DAMAGED));
  await shortage.getByRole("button", { name: "Eksik bildir" }).click();
  await expect(feedback(page)).toContainText(`${PSU}: ${DAMAGED} eksik bildirildi`);
  await scanShelf(page, ist.locations["Kablo & Konnektör"].code);
  await pickWith(CABLE, imported.variants[CABLE].barcode!, ORDERED[CABLE]);
  await page.getByRole("button", { name: "Görevi tamamla" }).click();
  await page.getByRole("alertdialog", { name: "Eksikle tamamlansın mı?" }).getByRole("button", { name: "Eksikle tamamla" }).click();
  await expect(page.getByText("Toplama tamamlandı", { exact: true })).toBeVisible();

  // ---- 6. Partial shipment: the 22 good PSUs + the cable.
  await page.getByRole("link", { name: "Sevkiyata git" }).click();
  await expect(page).toHaveURL(/\/depo\/sevkiyatlar\/[0-9a-f-]{36}$/);
  await packUnits(page, PSU, imported.variants[PSU].barcode!, ORDERED[PSU] - DAMAGED);
  await packUnits(page, CABLE, imported.variants[CABLE].barcode!, ORDERED[CABLE]);
  await packAndShip(page, "Demo Ambar (sentetik)", `DEMO-LED-${stamp}`);
  const partial = await demo.api.get<Order>(`/api/v1/orders/${orderId}`);
  expect(partial.order).toMatchObject({ status: "packed", fulfillmentStatus: "partial" });
  // The 3 damaged units stay reserved until the manager decides.
  await expectStock(page, ist.id, PSU, RECEIVED[PSU] - (ORDERED[PSU] - DAMAGED), DAMAGED, RECEIVED[PSU] - ORDERED[PSU]);

  // ---- 7. Short-close the rest on the order detail ("Kalanı kapat", #129): preset reason + note, then the API state.
  await page.goto(`/siparisler/${orderId}`);
  await page.getByRole("region", { name: "İşlemler" }).getByRole("button", { name: "Kalanı kapat" }).click();
  const closeDialog = page.getByRole("alertdialog");
  await expect(closeDialog.getByRole("list", { name: "Kapatılacak kalan miktar" })).toContainText(PSU);
  await closeDialog.getByLabel("Kapatma sebebi").selectOption("Diğer");
  await closeDialog.getByLabel("Açıklama").fill("Hasarlı 3 adet kapatıldı (sentetik)");
  const closeResponse = page.waitForResponse((r) => r.url().endsWith(`/api/v1/orders/${orderId}/close-remaining`) && r.request().method() === "POST");
  await closeDialog.getByRole("button", { name: "Kalanı kapat" }).click();
  expect((await closeResponse).status()).toBe(200);
  await expect(closeDialog).toHaveCount(0);
  const closed = await demo.api.get<Order>(`/api/v1/orders/${orderId}`);
  expect(closed.order.status).toBe("shipped");
  const psuLine = closed.lines.find((l) => l.sku === PSU)!;
  expect([Number(psuLine.shippedQuantity), Number(psuLine.cancelledQuantity)]).toEqual([ORDERED[PSU] - DAMAGED, DAMAGED]);
  await page.goto(`/siparisler/${orderId}`);
  await expect(page.getByText("Sevk Edildi").first()).toBeVisible();
  await expect(page.getByText(closed.order.fulfillmentStatus === "full" ? "Tamamı sevk edildi" : "Kısmi sevk").first()).toBeVisible();
  await expectStock(page, ist.id, PSU, RECEIVED[PSU] - (ORDERED[PSU] - DAMAGED), 0, RECEIVED[PSU] - (ORDERED[PSU] - DAMAGED));

  // ---- 8. Reports: top products, exported as CSV and XLSX (real files from the API).
  await page.goto("/raporlar/en-cok-siparis-edilenler");
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  const [csv] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "CSV indir" }).click()]);
  expect(csv.suggestedFilename()).toMatch(/\.csv$/);
  const csvText = await readFile((await csv.path())!, "utf8");
  expect(csvText).toContain("Stok Kodu");
  expect(csvText).toContain(PSU);
  await expect(page.getByText(/CSV dosyası indirildi/)).toBeVisible();
  const [xlsx] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Excel indir" }).click()]);
  expect(xlsx.suggestedFilename()).toMatch(/\.xlsx$/);
  const xlsxBytes = await readFile((await xlsx.path())!);
  expect(xlsxBytes.subarray(0, 2).toString("latin1")).toBe("PK");
  await page.screenshot({ path: "test-results/demo-led-report.png", fullPage: true });
});
