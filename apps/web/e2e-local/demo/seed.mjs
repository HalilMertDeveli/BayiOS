#!/usr/bin/env node
/**
 * Demo seeder CLI — docs/operations/demo.md.
 *
 *   bash infra/ci/e2e-stack.sh up          # local real stack (Supabase Auth + Postgres + API + web)
 *   npm --prefix apps/web run demo:seed     # default: the generic wholesale demo "Demo Toptan Ticaret" (--demo genel)
 *
 * Local stack only: refuses any non-local API/Auth URL. Demo logins are synthetic; their passwords are generated on
 * the first run and kept in the git-ignored .demo/credentials.json (never commit it). Re-running is idempotent: a demo
 * tenant that already exists (same slug) and is complete is left untouched.
 */
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { parseArgs } from "node:util";
import { DEFAULT_DEMO, DEMOS, seedDemo } from "./seeder.mjs";
import { REPO_ROOT, stackConfig } from "./stack.mjs";

const HELP = `Usage: npm --prefix apps/web run demo:seed -- [options]

Seeds fully synthetic demo businesses through the public BayiOS API of the LOCAL stack
(bash infra/ci/e2e-stack.sh up). Re-running is a no-op for a demo that is already complete.

Options:
  --demo <name>        ${Object.keys(DEMOS).join(" | ")} | all, comma-separated allowed (default: ${DEFAULT_DEMO})
                         genel  Demo Toptan Ticaret — generic wholesaler ("Sade başlangıç" template), team of
                                owner/manager/sales/warehouse staff, transfers, stock count (default demo)
                         motor  Demo Motor Yedek Parça — motorcycle spare parts (sector example)
                         led    Demo LED Elektronik — LED screens / electronics (sector example)
  --size <size>        full | small (default: full)
  --suffix <x>         separate copy: slug, name and every e-mail get the suffix (1–12 letters/digits)
  --credentials <file> generated passwords (default: .demo/credentials.json, git-ignored)
  --summary <file>     also write the summary (counts) as JSON
  --help               this text

Environment: E2E_API_URL, SUPABASE_URL, MAILPIT_URL (must be localhost), SUPABASE_PUBLISHABLE_KEY,
DEMO_PASSWORD (one password for every demo login, local only).`;

const { values } = parseArgs({
  options: {
    demo: { type: "string", default: DEFAULT_DEMO },
    size: { type: "string", default: "full" },
    credentials: { type: "string", default: join(REPO_ROOT, ".demo", "credentials.json") },
    summary: { type: "string" },
    // Separate copy of each demo (slug, name and e-mails get the suffix) — e.g. to try the seeder next to the real demo.
    suffix: { type: "string" },
    help: { type: "boolean", default: false },
  },
});

if (values.help) {
  console.log(HELP);
  process.exit(0);
}

const suffix = values.suffix?.toLowerCase();
if (suffix !== undefined && !/^[a-z0-9]{1,12}$/.test(suffix)) throw new Error("--suffix must be 1–12 letters/digits");
const suffixed = (email) => {
  if (!suffix) return email;
  const [local, domain] = email.split("@");
  return `${local}-${suffix}@${domain}`;
};
function withSuffix(def) {
  if (!suffix) return def;
  return {
    ...def,
    tenant: { name: `${def.tenant.name} ${suffix}`, slug: `${def.tenant.slug}-${suffix}` },
    owner: { ...def.owner, email: suffixed(def.owner.email) },
    team: (def.team ?? []).map((m) => ({ ...m, email: suffixed(m.email) })),
  };
}

const demos = values.demo === "all" ? Object.keys(DEMOS) : values.demo.split(",");
for (const d of demos) {
  if (!DEMOS[d]) {
    console.error(`Unknown demo "${d}" (${Object.keys(DEMOS).join(", ")}, all)\n\n${HELP}`);
    process.exit(2);
  }
}
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
const passwordFor = (demo, email, role) => {
  const key = `${demo}:${email}`;
  if (!credentials[key]) {
    credentials[key] = { email, role, password: process.env.DEMO_PASSWORD || `Demo-${randomBytes(12).toString("base64url")}-9`, note: "Synthetic local demo login. Never reuse." };
    changed = true;
  }
  return process.env.DEMO_PASSWORD || credentials[key].password;
};
const owners = {};
const teams = {};
const tenants = {};
for (const d of demos) {
  const def = withSuffix(DEMOS[d](values.size));
  owners[d] = { ...def.owner, password: passwordFor(d, def.owner.email, "owner") };
  teams[d] = (def.team ?? []).map((m) => ({ ...m, password: passwordFor(d, m.email, m.role) }));
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
    const result = await seedDemo({ cfg, demo: d, size: values.size, owner: owners[d], team: teams[d], tenant: tenants[d], log: (line) => console.log(line) });
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
console.log(`    passwords: ${credentialsFile}`);
for (const d of demos) {
  console.log(`  ${d.padEnd(6)} owner            ${owners[d].email}`);
  for (const m of teams[d]) console.log(`  ${"".padEnd(6)} ${m.role.padEnd(16)} ${m.email}`);
}
if (values.summary) writeFileSync(values.summary, `${JSON.stringify(results, null, 2)}\n`);
process.exit(failed ? 1 : 0);
