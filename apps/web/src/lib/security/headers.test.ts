import { describe, expect, it } from "vitest";
import { TURNSTILE_ORIGIN, contentSecurityPolicy, originOf, securityHeaders } from "../../../next.config";
import { TURNSTILE_ORIGIN as CAPTCHA_ORIGIN } from "@/lib/auth/captcha";

const directives = (csp: string) =>
  new Map(
    csp.split("; ").map((d) => {
      const [name, ...values] = d.split(" ");
      return [name, values] as const;
    }),
  );

describe("security headers (audit M-2)", () => {
  it("allows only the app, the API and Supabase as connect targets", () => {
    const csp = directives(
      contentSecurityPolicy({ apiBaseUrl: "https://api.bayios.example/v1/", supabaseUrl: "https://abc.supabase.co" }),
    );
    expect(csp.get("connect-src")).toEqual(["'self'", "https://api.bayios.example", "https://abc.supabase.co"]);
    expect(csp.get("default-src")).toEqual(["'self'"]);
    expect(csp.get("frame-ancestors")).toEqual(["'none'"]);
    expect(csp.get("object-src")).toEqual(["'none'"]);
    expect(csp.get("base-uri")).toEqual(["'none'"]);
    expect(csp.get("script-src")).toEqual(["'self'", "'unsafe-inline'"]);
    expect(csp.has("upgrade-insecure-requests")).toBe(true);
  });

  it("adds unsafe-eval only in development and skips the https upgrade on an http stack", () => {
    const csp = directives(
      contentSecurityPolicy({ apiBaseUrl: "http://localhost:5280", supabaseUrl: "http://127.0.0.1:54321", development: true }),
    );
    expect(csp.get("script-src")).toContain("'unsafe-eval'");
    expect(csp.has("upgrade-insecure-requests")).toBe(false);
    expect(csp.get("connect-src")).toEqual(["'self'", "http://localhost:5280", "http://127.0.0.1:54321"]);
  });

  it("never turns a missing or hostile value into a source", () => {
    expect(originOf(undefined)).toBeNull();
    expect(originOf("not a url")).toBeNull();
    expect(originOf("javascript:alert(1)")).toBeNull();
    expect(originOf("*")).toBeNull();
    // A value cannot smuggle extra directives: only the parsed origin is used.
    expect(originOf("https://api.example/x;script-src *")).toBe("https://api.example");
    const csp = contentSecurityPolicy({ apiBaseUrl: "*", supabaseUrl: "" });
    expect(directives(csp).get("connect-src")).toEqual(["'self'"]);
    expect(csp).not.toContain("*");
  });

  it("allows no foreign script or frame origin while CAPTCHA is off", () => {
    const csp = directives(contentSecurityPolicy({ supabaseUrl: "https://abc.supabase.co" }));
    expect(csp.get("frame-src")).toEqual(["'none'"]);
    expect(csp.get("script-src")).toEqual(["'self'", "'unsafe-inline'"]);
  });

  it("allows only the Turnstile origin for script and frame when CAPTCHA is on (#61)", () => {
    const csp = directives(contentSecurityPolicy({ supabaseUrl: "https://abc.supabase.co", turnstile: true }));
    expect(TURNSTILE_ORIGIN).toBe(CAPTCHA_ORIGIN);
    expect(csp.get("script-src")).toEqual(["'self'", "'unsafe-inline'", "https://challenges.cloudflare.com"]);
    expect(csp.get("frame-src")).toEqual(["https://challenges.cloudflare.com"]);
    // Nothing else widens: the widget talks to Cloudflare from inside its own frame.
    expect(csp.get("connect-src")).toEqual(["'self'", "https://abc.supabase.co"]);
    expect(csp.get("frame-ancestors")).toEqual(["'none'"]);
    expect(csp.get("default-src")).toEqual(["'self'"]);
  });

  it("sends the framing, sniffing, referrer and permission headers", () => {
    const headers = Object.fromEntries(securityHeaders({}).map((h) => [h.key, h.value]));
    expect(headers["X-Frame-Options"]).toBe("DENY");
    expect(headers["X-Content-Type-Options"]).toBe("nosniff");
    expect(headers["Referrer-Policy"]).toBe("no-referrer");
    expect(headers["Permissions-Policy"]).toContain("camera=()");
    expect(headers["Permissions-Policy"]).toContain("clipboard-write=(self)");
  });
});
