/**
 * Design-token guard (WCAG 2.2 AA, #61; UI kit palette, #222). Reads the real CSS — the generated raw palette
 * (tokens.css, from packages/design-tokens/*.json) and the semantic layer (globals.css) — resolves `var(--bos-…)`
 * references and checks the pairs the UI actually renders: text ≥ 4.5:1 (1.4.3), focus ring and button fills ≥ 3:1
 * (1.4.11 / 2.4.13 spirit), in the light theme and in both dark-theme blocks (which must stay identical). It also keeps
 * the Flutter semantic colours (apps/mobile/lib/core/theme/app_colors.dart) on the same values as the web light theme,
 * and the generated files in sync with the token JSON.
 */
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Comments stripped first: they contain braces (`color.{tone}.{fg,bg,border}`).
const strip = (path: string) => readFileSync(resolve(__dirname, path), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
const paletteCss = strip("tokens.css");
const css = strip("globals.css");

function block(source: string, selector: string): Map<string, string> {
  const start = source.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`selector not found: ${selector}`);
  const body = source.slice(start + selector.length + 2, source.indexOf("}", start));
  const vars = new Map<string, string>();
  for (const m of body.matchAll(/(--bos-[\w-]+)\s*:\s*([^;]+);/g)) vars.set(m[1], m[2].trim());
  return vars;
}

const rawVars = block(paletteCss, ":root");
const lightVars = new Map([...rawVars, ...block(css, ":root")]);
const darkMediaVars = block(css, ':root:not([data-theme="light"])');
const darkForcedVars = block(css, ':root[data-theme="dark"]');

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
  ["brand-on-panel", "brand-panel", 4.5],
  ["brand-on-panel-muted", "brand-panel", 4.5],
  ["brand-accent", "brand-panel", 3],
  ["white", "danger-solid", 4.5],
  ["white", "danger-solid-hover", 4.5],
  // The dark sidebar (same family in both themes): item text, section labels, active item.
  ["nav-fg", "nav-bg", 4.5],
  ["nav-fg", "nav-hover-bg", 4.5],
  ["nav-fg-muted", "nav-bg", 4.5],
  ["nav-fg-active", "nav-active-bg", 4.5],
  ["brand-accent", "nav-bg", 3],
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

  it("uses the UI kit green family and surfaces (#222), not the retired petrol palette", () => {
    const light = theme(new Map());
    expect(light("--bos-brand-accent")).toBe("#27a376");
    expect(light("--bos-brand-primary")).toBe("#1f825e");
    expect(light("--bos-text-primary")).toBe("#0c110f");
    expect(light("--bos-surface-page")).toBe("#f4f6f6");
    expect(light("--bos-surface-card")).toBe("#ffffff");
    expect(light("--bos-border-default")).toBe("#e6e8e5");
    expect(css).not.toMatch(/petrol|copper/);
  });

  it("keeps the generated token files in sync with packages/design-tokens", () => {
    // Throws (exit 1) when tokens.css or app_palette.dart is stale.
    execFileSync(process.execPath, [resolve(__dirname, "../../../../packages/design-tokens/build.mjs"), "--check"]);
  });

  it("mirrors the brand, text, surface and border tokens in the Flutter theme", () => {
    const mobile = resolve(__dirname, "../../../mobile/lib/core/theme");
    const palette = readFileSync(resolve(mobile, "app_palette.dart"), "utf8");
    const colors = readFileSync(resolve(mobile, "app_colors.dart"), "utf8");
    const lightStart = colors.indexOf("static const light = AppColors(");
    if (lightStart < 0) throw new Error("AppColors.light not found");
    const lightBlock = colors.slice(lightStart, colors.indexOf(");", lightStart));
    const hexOf = (expr: string): string => {
      const literal = /^Color\(0xFF([0-9A-Fa-f]{6})\)$/.exec(expr);
      if (literal) return `#${literal[1].toLowerCase()}`;
      const ref = /^AppPalette\.(\w+)$/.exec(expr);
      if (!ref) throw new Error(`unsupported colour expression: ${expr}`);
      const m = new RegExp(`static const ${ref[1]} = Color\\(0xFF([0-9A-Fa-f]{6})\\)`).exec(palette);
      if (!m) throw new Error(`AppPalette.${ref[1]} not found`);
      return `#${m[1].toLowerCase()}`;
    };
    const dartColor = (name: string) => {
      const m = new RegExp(`\\b${name}:\\s*([^,\\n]+),`).exec(lightBlock);
      if (!m) throw new Error(`AppColors.light.${name} not found`);
      return hexOf(m[1].trim());
    };
    const light = theme(new Map());
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
