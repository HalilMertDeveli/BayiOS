import { describe, expect, it } from "vitest";
import { DEFAULT_SECTOR, SLUG_PATTERN, sectorOptions, slugify, validateSlug, validateTenantName } from "./tenant-form";

describe("starter templates (sector-agnostic, owner direction 2026-11-09)", () => {
  it("pre-selects the plain start and lists it first", () => {
    expect(DEFAULT_SECTOR).toBe("general");
    expect(sectorOptions[0].value).toBe("general");
  });

  it("names no industry in the options shown to a new business", () => {
    const text = sectorOptions.map((o) => `${o.label} ${o.description}`).join(" ");
    expect(text).not.toMatch(/motosiklet|motor|\bLED\b|yedek parça|elektronik/i);
  });
});

describe("slugify", () => {
  it.each([
    ["Yıldız Toptan Gıda Ltd. Şti.", "yildiz-toptan-gida-ltd-sti"],
    ["İSTANBUL Kırtasiye & Ofis", "istanbul-kirtasiye-ofis"],
    ["  Çağrı Öztürk  ", "cagri-ozturk"],
    ["---", ""],
  ])("%s → %s", (input, expected) => {
    expect(slugify(input)).toBe(expected);
  });

  it("produces slugs the API accepts", () => {
    const slug = slugify("Ğüşiöç Toptan ".repeat(10));
    expect(slug.length).toBeLessThanOrEqual(63);
    expect(SLUG_PATTERN.test(slug)).toBe(true);
  });
});

describe("validators", () => {
  it("validates the business name length", () => {
    expect(validateTenantName(" ")).toBe("İşletme adını girin.");
    expect(validateTenantName("A")).toBe("İşletme adı en az 2 karakter olmalı.");
    expect(validateTenantName("AB")).toBeUndefined();
    expect(validateTenantName("x".repeat(201))).toBe("İşletme adı en fazla 200 karakter olabilir.");
  });

  it("mirrors the API slug rule", () => {
    expect(validateSlug("ab")).toBe("İşletme adresi 3–63 karakter olmalı.");
    expect(validateSlug("-abc")).toContain("tire ile başlayamaz");
    expect(validateSlug("Abc")).toContain("küçük harf");
    expect(validateSlug("abc-1")).toBeUndefined();
  });
});
