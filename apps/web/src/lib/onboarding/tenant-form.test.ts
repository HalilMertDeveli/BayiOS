import { describe, expect, it } from "vitest";
import { SLUG_PATTERN, slugify, validateSlug, validateTenantName } from "./tenant-form";

describe("slugify", () => {
  it.each([
    ["Yılmaz Motor Yedek Parça Ltd. Şti.", "yilmaz-motor-yedek-parca-ltd-sti"],
    ["İSTANBUL LED & Elektronik", "istanbul-led-elektronik"],
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
