#!/usr/bin/env node
/**
 * Pilot demo seeder CLI (#56/#57) — docs/operations/demo.md.
 *
 *   bash infra/ci/e2e-stack.sh up          # local real stack (Supabase Auth + Postgres + API + web)
 *   npm --prefix apps/web run demo:seed     # both demos; --demo motor|led, --size full|small
 *
 * Local stack only: refuses any non-local API/Auth URL. Demo logins are synthetic; their passwords are generated on
 * the first run and kept in the git-ignored .demo/credentials.json (never commit it). Re-running is idempotent: a demo
 * tenant that already exists (same slug) and is complete is left untouched.
 */
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { parseArgs } from "node:util";
import { DEMOS, seedDemo } from "./seeder.mjs";
import { REPO_ROOT, stackConfig } from "./stack.mjs";

const { values } = parseArgs({
  options: {
    demo: { type: "string", default: "all" },
    size: { type: "string", default: "full" },
    credentials: { type: "string", default: join(REPO_ROOT, ".demo", "credentials.json") },
    summary: { type: "string" },
    // Separate copy of each demo (slug, name and owner e-mail get the suffix) — e.g. to try the seeder next to the real demo.
    suffix: { type: "string" },
  },
});

const suffix = values.suffix?.toLowerCase();
if (suffix !== undefined && !/^[a-z0-9]{1,12}$/.test(suffix)) throw new Error("--suffix must be 1–12 letters/digits");
function withSuffix(def) {
  if (!suffix) return def;
  const [local, domain] = def.owner.email.split("@");
  return { ...def, tenant: { name: `${def.tenant.name} ${suffix}`, slug: `${def.tenant.slug}-${suffix}` }, owner: { ...def.owner, email: `${local}-${suffix}@${domain}` } };
}

const demos = values.demo === "all" ? Object.keys(DEMOS) : values.demo.split(",");
for (const d of demos) if (!DEMOS[d]) throw new Error(`Unknown demo "${d}" (motor, led, all)`);
if (!["full", "small"].includes(values.size)) throw new Error(`--size must be full or small`);

const cfg = stackConfig();
for (const url of [cfg.apiUrl, cfg.supabaseUrl, cfg.mailpitUrl]) {
  const host = new URL(url).hostname;
  if (!["localhost", "127.0.0.1", "::1"].includes(host)) {
    console.error(`Refusing to seed ${url}: the demo seeder only runs against the local stack.`);
    process.exit(2);
  }
}

// Generated passwords (git-ignored file); DEMO_PASSWORD overrides for all demo users.
const credentialsFile = values.credentials;
const credentials = existsSync(credentialsFile) ? JSON.parse(readFileSync(credentialsFile, "utf8")) : {};
let changed = false;
const owners = {};
const tenants = {};
for (const d of demos) {
  const def = withSuffix(DEMOS[d](values.size));
  const key = `${d}:${def.owner.email}`;
  if (!credentials[key]) {
    credentials[key] = { email: def.owner.email, password: process.env.DEMO_PASSWORD || `Demo-${randomBytes(12).toString("base64url")}-9`, note: "Synthetic local demo login. Never reuse." };
    changed = true;
  }
  owners[d] = { ...def.owner, password: process.env.DEMO_PASSWORD || credentials[key].password };
  tenants[d] = def.tenant;
}
if (changed) {
  mkdirSync(dirname(credentialsFile), { recursive: true });
  writeFileSync(credentialsFile, `${JSON.stringify(credentials, null, 2)}\n`, { mode: 0o600 });
}

const results = {};
let failed = false;
for (const d of demos) {
  try {
    const result = await seedDemo({ cfg, demo: d, size: values.size, owner: owners[d], tenant: tenants[d], log: (line) => console.log(line) });
    results[d] = { slug: tenants[d].slug, skipped: result.skipped, seconds: Math.round(result.seconds * 10) / 10, counts: result.counts };
  } catch (error) {
    failed = true;
    console.error(`\n[${d}] seeding failed: ${error instanceof Error ? error.message : String(error)}`);
    results[d] = { error: String(error instanceof Error ? error.message : error) };
  }
}

console.log("\n==> Demo summary");
console.log(JSON.stringify(results, null, 2));
console.log("\n==> Demo logins (synthetic, local stack only) — web: http://localhost:3200/giris (or E2E_WEB_PORT)");
for (const d of demos) console.log(`  ${d.padEnd(6)} ${owners[d].email}  (password: ${credentialsFile})`);
if (values.summary) writeFileSync(values.summary, `${JSON.stringify(results, null, 2)}\n`);
process.exit(failed ? 1 : 0);
