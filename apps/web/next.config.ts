import type { NextConfig } from "next";

/*
 * HTTP security headers for every web response (security audit 2026-11 M-2, threat-model §3.1 / §3.5), applied by
 * `headers()` below, so they also cover static assets, error pages and proxy redirects. Kept in this file on purpose:
 * `next start` on Node 24 loads next.config.ts through Next's CommonJS transpiler, which cannot resolve a local
 * TypeScript import (it worked on Node 26's native TS loader, failed in CI). Unit tests: src/lib/security/headers.test.ts.
 *
 * Why no nonce for scripts: the app uses Cache Components (`cacheComponents: true`). Almost every route is
 * prerendered at build time into a static shell (and the `[id]` routes are Partial Prerenders), and Next's
 * inline bootstrap / RSC payload scripts in that shell are fixed at build time — a per-request nonce cannot be
 * stamped into them (Next.js CSP guide: "PPR is incompatible with nonce-based CSP"). Hashing those inline
 * scripts is not robust either: they differ per route and per build, and PPR routes stream more inline scripts
 * at request time. So script-src is `'self' 'unsafe-inline'`: no foreign script origin, no `eval` in
 * production, no plugins, no <base> hijack, no framing, and `connect-src`/`img-src` limited to the app,
 * the BayiOS API and Supabase Auth — which removes the fetch/beacon exfiltration channels for a stolen token.
 * The remaining XSS defence for inline injection is React's escaping + the `react/no-danger` lint rule.
 * Residual risk and the path to a nonce CSP: docs/security/audit-2026-11.md (M-2).
 */
export type SecurityHeaderEnv = {
  /** NEXT_PUBLIC_API_BASE_URL — the only API the browser calls. */
  apiBaseUrl?: string;
  /** NEXT_PUBLIC_SUPABASE_URL — Supabase Auth (identity only). */
  supabaseUrl?: string;
  /** `next dev`: React needs `eval` for its debugging features (never in production). */
  development?: boolean;
  /**
   * NEXT_PUBLIC_TURNSTILE_SITE_KEY is set: the auth forms show Cloudflare Turnstile (OD-05, #61), so its script and
   * challenge-frame origin — and nothing else — is allowed. Unset (local/CI/e2e) = no foreign origin at all. Site-wide
   * rather than per auth route on purpose: a client-side navigation into /giris keeps the CSP of the first document.
   */
  turnstile?: boolean;
};

/** Cloudflare Turnstile (script + challenge iframe). Same value as TURNSTILE_ORIGIN in src/lib/auth/captcha.ts. */
export const TURNSTILE_ORIGIN = "https://challenges.cloudflare.com";

export type Header = { key: string; value: string };

/** Origin of an absolute http(s) URL, or null for a missing / malformed value (it then simply is not allowed). */
export function originOf(value: string | undefined): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:" ? url.origin : null;
  } catch {
    return null;
  }
}

export function contentSecurityPolicy(env: SecurityHeaderEnv): string {
  const origins = [originOf(env.apiBaseUrl), originOf(env.supabaseUrl)].filter((o): o is string => o !== null);
  const connect = ["'self'", ...new Set(origins)];
  const allHttps = origins.length > 0 && origins.every((o) => o.startsWith("https://"));
  const turnstile = env.turnstile ? [TURNSTILE_ORIGIN] : [];

  const directives: [string, ...string[]][] = [
    ["default-src", "'self'"],
    ["script-src", "'self'", "'unsafe-inline'", ...(env.development ? ["'unsafe-eval'"] : []), ...turnstile],
    // Next's default error pages carry inline style attributes/tags; Tailwind CSS is a same-origin file.
    ["style-src", "'self'", "'unsafe-inline'"],
    // blob: = report downloads (URL.createObjectURL); data: = inline SVG/data images.
    ["img-src", "'self'", "data:", "blob:"],
    ["font-src", "'self'"], // next/font self-hosts the fonts
    ["connect-src", ...connect],
    ["object-src", "'none'"],
    ["base-uri", "'none'"],
    ["form-action", "'self'"],
    turnstile.length > 0 ? ["frame-src", ...turnstile] : ["frame-src", "'none'"],
    ["frame-ancestors", "'none'"],
    ["manifest-src", "'self'"],
    ["worker-src", "'self'"],
  ];
  // Only when everything is served over TLS: on the local/CI http stack it would break the API calls.
  if (allHttps) directives.push(["upgrade-insecure-requests"]);
  return directives.map((d) => d.join(" ")).join("; ");
}

/**
 * Camera, microphone, location, payment, USB… are denied: the web app scans with keyboard-wedge (HID) scanners,
 * not the camera. The clipboard stays available to the app itself ("copy invitation link"), never to frames.
 */
export const PERMISSIONS_POLICY = [
  "camera=()",
  "microphone=()",
  "geolocation=()",
  "payment=()",
  "usb=()",
  "serial=()",
  "hid=()",
  "bluetooth=()",
  "midi=()",
  "display-capture=()",
  "browsing-topics=()",
  // Same-origin only (the browser default allowlist, stated explicitly); reading still needs the user's grant.
  // The team e2e test verifies the copied invitation link by reading it back.
  "clipboard-read=(self)",
  "clipboard-write=(self)",
].join(", ");

export function securityHeaders(env: SecurityHeaderEnv): Header[] {
  return [
    { key: "Content-Security-Policy", value: contentSecurityPolicy(env) },
    // Legacy browsers that ignore frame-ancestors.
    { key: "X-Frame-Options", value: "DENY" },
    { key: "X-Content-Type-Options", value: "nosniff" },
    // Threat-model T7: no URL (path, query) ever leaves the app in a Referer.
    { key: "Referrer-Policy", value: "no-referrer" },
    { key: "Permissions-Policy", value: PERMISSIONS_POLICY },
    { key: "Cross-Origin-Opener-Policy", value: "same-origin" },
    // Browsers ignore HSTS on plain http (local/CI), so it is safe to always send; no includeSubDomains/preload —
    // that is a decision for the production domain owner, not for the app.
    { key: "Strict-Transport-Security", value: "max-age=31536000" },
  ];
}

const nextConfig: NextConfig = {
  /* config options here */
  // Container image only (apps/web/Dockerfile sets BAYIOS_NEXT_OUTPUT=standalone): a self-contained server.js with a
  // traced, minimal node_modules. Off by default so `next start` (CI, e2e) and Vercel builds stay unchanged.
  output: process.env.BAYIOS_NEXT_OUTPUT === "standalone" ? "standalone" : undefined,
  cacheComponents: true,
  partialPrefetching: true,
  // No "X-Powered-By: Next.js" (security audit 2026-11 M-2).
  poweredByHeader: false,
  async headers() {
    return [
      {
        source: "/:path*",
        headers: securityHeaders({
          apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL,
          supabaseUrl: process.env.NEXT_PUBLIC_SUPABASE_URL,
          development: process.env.NODE_ENV === "development",
          turnstile: Boolean(process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY?.trim()),
        }),
      },
    ];
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
