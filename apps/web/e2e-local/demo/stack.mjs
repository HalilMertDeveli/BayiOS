import { execSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, sep } from "node:path";

/**
 * Access to the local real stack (infra/ci/e2e-stack.sh) for the demo seeder: Supabase Auth (GoTrue behind Kong) +
 * Mailpit for the confirmation mail, and the BayiOS API. Everything goes through public endpoints with the demo
 * owner's own Supabase token — the seeder never touches a database table directly.
 */

/** Repo root, found from the working directory (no import.meta: Playwright loads this file as CommonJS too). */
function findRepoRoot(start = process.cwd()) {
  for (let dir = start; ; dir = dirname(dir)) {
    if (existsSync(join(dir, "infra", "supabase", "local", "supabase", "config.toml"))) return dir.endsWith(sep) ? dir : dir + sep;
    if (dirname(dir) === dir) throw new Error(`Run from inside the BayiOS repository (no infra/supabase/local above ${start}).`);
  }
}

export const REPO_ROOT = findRepoRoot();
const SUPABASE_CLI = process.env.SUPABASE_CLI ?? "supabase@2.122.1";

/**
 * The publishable key is the public local-development key of the CLI stack. Order: env (CI sets it), the web app's
 * git-ignored apps/web/.env.local, then `supabase status`.
 */
function discoverPublishableKey() {
  const envLocal = `${REPO_ROOT}apps/web/.env.local`;
  if (existsSync(envLocal)) {
    const match = readFileSync(envLocal, "utf8").match(/^NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=(.+)$/m);
    if (match) return match[1].trim().replace(/^["']|["']$/g, "");
  }
  const workdir = `${REPO_ROOT}infra/supabase/local`;
  let output;
  try {
    output = execSync(`npx -y ${SUPABASE_CLI} status -o json --workdir "${workdir}"`, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
  } catch {
    throw new Error(
      "Could not read the Supabase publishable key: is the stack running (bash infra/ci/e2e-stack.sh up)? Or set SUPABASE_PUBLISHABLE_KEY " +
        "(the 'Publishable' value of `npx -y supabase@2.122.1 status --workdir infra/supabase/local`).",
    );
  }
  const status = JSON.parse(output.slice(output.indexOf("{")));
  const key = status.PUBLISHABLE_KEY || status.ANON_KEY;
  if (!key) throw new Error("Could not read the publishable key from 'supabase status'.");
  return key;
}

let cachedKey;

export function stackConfig(env = process.env) {
  return {
    apiUrl: env.E2E_API_URL ?? "http://localhost:5280",
    supabaseUrl: env.SUPABASE_URL ?? "http://127.0.0.1:54321",
    mailpitUrl: env.MAILPIT_URL ?? "http://127.0.0.1:54324",
    get publishableKey() {
      cachedKey ??= env.SUPABASE_PUBLISHABLE_KEY || env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY || discoverPublishableKey();
      return cachedKey;
    },
  };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function gotrue(cfg, path, body) {
  const response = await fetch(`${cfg.supabaseUrl}/auth/v1${path}`, {
    method: "POST",
    headers: { apikey: cfg.publishableKey, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

/** The newest confirmation link Mailpit received for the address (GoTrue /verify). */
async function confirmationLink(cfg, email, notBefore) {
  for (let attempt = 0; attempt < 60; attempt++) {
    const search = await fetch(`${cfg.mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`);
    const { messages = [] } = await search.json();
    const fresh = messages.find((m) => !notBefore || Date.parse(m.Created) >= notBefore - 5_000);
    if (fresh) {
      const message = await (await fetch(`${cfg.mailpitUrl}/api/v1/message/${fresh.ID}`)).json();
      const link = message.Text.match(/https?:\/\/\S+\/verify\?\S+/)?.[0];
      if (link) return link.replace(/&amp;/g, "&");
    }
    await sleep(250);
  }
  throw new Error(`No confirmation e-mail for ${email} in Mailpit (${cfg.mailpitUrl}).`);
}

async function confirmByMail(cfg, email, notBefore) {
  const link = await confirmationLink(cfg, email, notBefore);
  // GoTrue verifies the token and redirects to the site URL; the redirect itself is not needed.
  const response = await fetch(link, { redirect: "manual" });
  if (response.status >= 400) throw new Error(`Confirmation link for ${email} answered ${response.status}`);
}

async function passwordGrant(cfg, email, password) {
  return gotrue(cfg, "/token?grant_type=password", { email, password });
}

/**
 * Signs the user in; on first use signs up (Supabase Auth) and confirms the e-mail through Mailpit — the same path a
 * real user takes, without the browser. Returns the access token.
 * @param {{ email: string, password: string, fullName: string }} user
 */
export async function ensureUser(cfg, user) {
  let grant = await passwordGrant(cfg, user.email, user.password);
  if (grant.status === 200) return grant.body.access_token;
  const code = grant.body?.error_code ?? grant.body?.code ?? grant.body?.error;

  if (code === "email_not_confirmed") {
    const started = Date.now();
    await gotrue(cfg, "/resend", { type: "signup", email: user.email });
    await confirmByMail(cfg, user.email, started);
  } else {
    const started = Date.now();
    const signUp = await gotrue(cfg, "/signup", { email: user.email, password: user.password, data: { full_name: user.fullName } });
    if (signUp.status >= 400) throw new Error(`Sign-up of ${user.email} failed: ${signUp.status} ${JSON.stringify(signUp.body)}`);
    if (Array.isArray(signUp.body?.identities) && signUp.body.identities.length === 0) {
      throw new Error(
        `${user.email} already exists in local Supabase Auth with another password. Restore the password from the git-ignored ` +
          "credentials file (.demo/credentials.json), or reset local Auth: E2E_SUPABASE_NO_BACKUP=1 bash infra/ci/e2e-stack.sh down.",
      );
    }
    await confirmByMail(cfg, user.email, started);
  }

  grant = await passwordGrant(cfg, user.email, user.password);
  if (grant.status !== 200) throw new Error(`Sign-in of ${user.email} failed: ${grant.status} ${JSON.stringify(grant.body)}`);
  return grant.body.access_token;
}

export class ApiError extends Error {
  constructor(method, path, status, body) {
    super(`${method} ${path} → ${status}: ${JSON.stringify(body)?.slice(0, 1500)}`);
    this.status = status;
    this.body = body;
  }
}

/** BayiOS API with the user's Bearer token and (optionally) the active tenant. */
export class Api {
  constructor(cfg, token, tenantId = null) {
    this.cfg = cfg;
    this.token = token;
    this.tenantId = tenantId;
  }

  withTenant(tenantId) {
    return new Api(this.cfg, this.token, tenantId);
  }

  /** The signed-in user's credentials in the shape the Playwright helpers use (support/real-stack.ts ApiSession). */
  get session() {
    return { authorization: `Bearer ${this.token}`, tenantId: this.tenantId };
  }

  /**
   * One request. Writes carry a fresh Idempotency-Key; a transient lock conflict (409 + Retry-After) is retried with
   * the SAME key, exactly as the API asks clients to do.
   * @returns {Promise<{ status: number, body: any, etag: string | null, headers: Headers }>}
   */
  async raw(method, path, { body, ifMatch, contentType, form, expect } = {}) {
    const headers = { Authorization: `Bearer ${this.token}`, Accept: "application/json" };
    if (this.tenantId) headers["X-Tenant-Id"] = this.tenantId;
    if (method !== "GET") headers["Idempotency-Key"] = randomUUID();
    if (ifMatch) headers["If-Match"] = ifMatch;
    let payload;
    if (form) payload = form;
    else if (body !== undefined) {
      headers["Content-Type"] = contentType ?? "application/json";
      payload = JSON.stringify(body);
    }
    for (let attempt = 0; ; attempt++) {
      const response = await fetch(`${this.cfg.apiUrl}${path}`, { method, headers, body: payload });
      const text = await response.text();
      let parsed = null;
      try {
        parsed = text ? JSON.parse(text) : null;
      } catch {
        parsed = text;
      }
      const retryAfter = Number(response.headers.get("retry-after") ?? "0");
      if (response.status === 409 && retryAfter > 0 && retryAfter <= 5 && attempt < 5) {
        await sleep(retryAfter * 1000);
        continue;
      }
      const result = { status: response.status, body: parsed, etag: response.headers.get("etag"), headers: response.headers };
      const ok = expect ? expect.includes(response.status) : response.status < 400;
      if (!ok) throw new ApiError(method, path, response.status, parsed);
      return result;
    }
  }

  async get(path) {
    return (await this.raw("GET", path)).body;
  }

  async post(path, body, options = {}) {
    return (await this.raw("POST", path, { body, ...options })).body;
  }

  async put(path, body, options = {}) {
    return (await this.raw("PUT", path, { body, ...options })).body;
  }

  /** JSON merge patch with the current version (GET → ETag → PATCH If-Match). */
  async patch(path, body, getPath = path) {
    const current = await this.raw("GET", getPath);
    return (await this.raw("PATCH", path, { body, ifMatch: current.etag, contentType: "application/merge-patch+json" })).body;
  }

  /** Every item of a cursor-paged list. */
  async all(path, limit = 100) {
    const items = [];
    let cursor = null;
    const separator = path.includes("?") ? "&" : "?";
    do {
      const page = await this.get(`${path}${separator}limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`);
      items.push(...page.items);
      cursor = page.page?.hasMore ? page.page.nextCursor : null;
    } while (cursor);
    return items;
  }
}

export const etagOf = (version) => `W/"${version}"`;
export { sleep };
