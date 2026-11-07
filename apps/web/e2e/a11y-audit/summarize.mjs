// Aggregates test-results/a11y-audit/*.json (written by all-routes.audit.ts) into a Markdown findings table on stdout.
// Usage (from apps/web): node e2e/a11y-audit/summarize.mjs > ../../.tmp-a11y.md
import fs from "node:fs";
import path from "node:path";

const dir = path.resolve(process.cwd(), "test-results", "a11y-audit");
const files = fs.readdirSync(dir).filter((f) => f.endsWith(".json"));
const records = files.map((f) => JSON.parse(fs.readFileSync(path.join(dir, f), "utf8")));
const order = { critical: 0, serious: 1, moderate: 2, minor: 3 };

/** rule id → { impact, help, tags, routes: Map<route, {viewports:Set, nodes:Set}> } */
const rules = new Map();
for (const r of records) {
  for (const v of r.violations) {
    const rule = rules.get(v.id) ?? { impact: v.impact, help: v.help, tags: v.tags, helpUrl: v.helpUrl, routes: new Map() };
    const at = rule.routes.get(r.route) ?? { viewports: new Set(), nodes: new Set() };
    at.viewports.add(r.viewport);
    for (const n of v.nodes) at.nodes.add(n.target);
    rule.routes.set(r.route, at);
    rules.set(v.id, rule);
  }
}

const sorted = [...rules.entries()].sort((a, b) => order[a[1].impact] - order[b[1].impact] || a[0].localeCompare(b[0]));
const counts = { critical: 0, serious: 0, moderate: 0, minor: 0 };
for (const [, rule] of sorted) counts[rule.impact] += rule.routes.size;

console.log(`Scanned: ${records.length} route×viewport pages (${new Set(records.map((r) => r.route)).size} routes), axe ${records[0]?.axe}`);
console.log(`Pages with zero violations: ${records.filter((r) => r.violations.length === 0).length}`);
console.log(`Distinct rules violated: ${sorted.length}; (rule × route) findings by impact: ${JSON.stringify(counts)}\n`);
console.log("| # | Impact | axe rule | WCAG | Routes (viewports) | Example nodes |");
console.log("|---|---|---|---|---|---|");
let i = 0;
for (const [id, rule] of sorted) {
  i++;
  const routes = [...rule.routes.entries()].map(([route, at]) => `${route} (${[...at.viewports].join("+")})`);
  const nodes = [...new Set([...rule.routes.values()].flatMap((at) => [...at.nodes]))].slice(0, 3).map((n) => `\`${n.replaceAll("|", "\\|")}\``);
  console.log(`| ${i} | ${rule.impact} | \`${id}\` — ${rule.help} | ${rule.tags.join(", ") || "best-practice (no SC)"} | ${routes.join("; ")} | ${nodes.join("<br>")} |`);
}
const incomplete = new Map();
for (const r of records) for (const v of r.incomplete) incomplete.set(v.id, (incomplete.get(v.id) ?? 0) + v.count);
console.log(`\nNeeds manual review (axe "incomplete"): ${[...incomplete.entries()].map(([id, n]) => `${id} ×${n}`).join(", ") || "none"}`);
