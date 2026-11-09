/**
 * Design-token contrast guard (WCAG 2.2 AA, #61). Reads the real globals.css, resolves `var(--bos-…)` references and checks
 * the pairs the UI actually renders: text ≥ 4.5:1 (1.4.3), focus ring and button fills ≥ 3:1 (1.4.11 / 2.4.13 spirit),
 * in the light theme and in both dark-theme blocks (which must stay identical). It also keeps the Flutter tokens
 * (apps/mobile/lib/core/ui/tokens.dart) on the same brand/text/border values as the web light theme.
 */
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Comments stripped first: they contain braces (`color.{tone}.{fg,bg,border}`).
const css = readFileSync(resolve(__dirname, "globals.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

function block(selector: string): Map<string, string> {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`selector not found: ${selector}`);
  const body = css.slice(start + selector.length + 2, css.indexOf("}", start));
  const vars = new Map<string, string>();
  for (const m of body.matchAll(/(--bos-[\w-]+)\s*:\s*([^;]+);/g)) vars.set(m[1], m[2].trim());
  return vars;
}

const lightVars = block(":root");
const darkMediaVars = block(':root:not([data-theme="light"])');
const darkForcedVars = block(':root[data-theme="dark"]');

function theme(overrides: Map<string, string>) {
  const merged = new Map([...lightVars, ...overrides]);
  const get = (name: string, depth = 0): string => {
    // The danger button renders `text-white` on danger-solid.
    const value = name === "--bos-white" ? "#ffffff" : merged.get(name);
    if (value === undefined) throw new Error(`token not defined: ${name}`);
    const ref = /^var\((--bos-[\w-]+)\)$/.exec(value);
    if (ref && depth < 5) return get(ref[1], depth + 1);
    return value.toLowerCase();
  };
  return get;
}

function luminance(hex: string): number {
  const m = /^#([0-9a-f]{6})$/.exec(hex);
  if (!m) throw new Error(`not a 6-digit hex colour: ${hex}`);
  const [r, g, b] = [0, 2, 4].map((i) => {
    const c = parseInt(m[1].slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const SURFACES = ["surface-page", "surface-card", "surface-sunken"];
const TONES = ["neutral", "info", "success", "warning", "danger", "purple", "teal", "brand", "ai"];

/** [foreground token, background token, minimum ratio] */
const PAIRS: [string, string, number][] = [
  ...SURFACES.flatMap((s): [string, string, number][] => [
    ["text-primary", s, 4.5],
    ["text-secondary", s, 4.5],
    ["text-muted", s, 4.5],
    ["text-link", s, 4.5],
    ["brand-fg", s, 4.5],
    ["border-focus", s, 3],
    ["brand-primary", s, 3],
    ["border-strong", s, 3],
  ]),
  ["brand-on-primary", "brand-primary", 4.5],
  ["brand-on-primary", "brand-primary-hover", 4.5],
  ["brand-primary", "brand-primary-subtle", 4.5],
  ["brand-on-accent", "brand-accent", 4.5],
  ["brand-on-accent", "brand-accent-hover", 4.5],
  ["white", "danger-solid", 4.5],
  ["white", "danger-solid-hover", 4.5],
  ...TONES.map((t): [string, string, number] => [`${t}-fg`, `${t}-bg`, 4.5]),
];

describe.each([
  ["light", theme(new Map())],
  ["dark", theme(darkMediaVars)],
])("design tokens — %s theme", (_name, get) => {
  it.each(PAIRS)("%s on %s ≥ %d:1", (fg, bg, min) => {
    const ratio = contrast(get(`--bos-${fg}`), get(`--bos-${bg}`));
    expect(ratio, `${fg} ${get(`--bos-${fg}`)} on ${bg} ${get(`--bos-${bg}`)} = ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(min);
  });
});

describe("design tokens — structure", () => {
  it("keeps the two dark-theme blocks identical", () => {
    expect(Object.fromEntries(darkForcedVars)).toEqual(Object.fromEntries(darkMediaVars));
  });

  it("uses the petrol primary, not the old navy or generic blue", () => {
    const light = theme(new Map());
    expect(light("--bos-brand-primary")).toBe("#0c5468");
    expect(light("--bos-border-focus")).not.toBe("#2563eb");
  });

  it("mirrors the brand, text, surface and border tokens in the Flutter theme", () => {
    const dart = readFileSync(resolve(__dirname, "../../../mobile/lib/core/ui/tokens.dart"), "utf8");
    const light = theme(new Map());
    const dartColor = (name: string) => {
      const m = new RegExp(`static const ${name} = Color\\(0xFF([0-9A-Fa-f]{6})\\)`).exec(dart);
      if (!m) throw new Error(`BosColors.${name} not found`);
      return `#${m[1].toLowerCase()}`;
    };
    const mapping: Record<string, string> = {
      brandPrimary: "--bos-brand-primary",
      brandPrimaryHover: "--bos-brand-primary-hover",
      brandPrimarySubtle: "--bos-brand-primary-subtle",
      brandOnPrimary: "--bos-brand-on-primary",
      brandAccent: "--bos-brand-accent",
      brandOnAccent: "--bos-brand-on-accent",
      textPrimary: "--bos-text-primary",
      textSecondary: "--bos-text-secondary",
      textMuted: "--bos-text-muted",
      textLink: "--bos-text-link",
      surfacePage: "--bos-surface-page",
      surfaceCard: "--bos-surface-card",
      surfaceSunken: "--bos-surface-sunken",
      borderDefault: "--bos-border-default",
      borderStrong: "--bos-border-strong",
      borderFocus: "--bos-border-focus",
    };
    for (const [dartName, token] of Object.entries(mapping)) expect(dartColor(dartName), dartName).toBe(light(token));
  });
});
