import { describe, expect, it } from "vitest";
import { isGuestOnlyPath, isPublicPath, safeNextPath, signInUrl } from "./routes";

describe("safeNextPath", () => {
  it.each([
    ["/urunler", "/urunler"],
    ["/urunler?q=vida#ust", "/urunler?q=vida#ust"],
  ])("keeps same-origin path %s", (input, expected) => {
    expect(safeNextPath(input)).toBe(expected);
  });

  it.each([null, "", "https://evil.example", "//evil.example/x", "/\\evil.example", "javascript:alert(1)", "urunler"])(
    "rejects %s",
    (input) => {
      expect(safeNextPath(input)).toBe("/pano");
    },
  );
});

describe("route groups", () => {
  it("only the landing, auth screens and callback are public", () => {
    for (const path of ["/", "/giris", "/kayit", "/sifre-sifirla", "/auth/callback"]) expect(isPublicPath(path)).toBe(true);
    for (const path of ["/pano", "/isletme-olustur", "/isletme-sec", "/sifre-yenile", "/tasarim-sistemi"]) {
      expect(isPublicPath(path)).toBe(false);
    }
  });

  it("sign-in and sign-up are guest-only", () => {
    expect(isGuestOnlyPath("/giris")).toBe(true);
    expect(isGuestOnlyPath("/kayit")).toBe(true);
    expect(isGuestOnlyPath("/sifre-sifirla")).toBe(false);
  });

  it("builds the sign-in URL with an encoded next", () => {
    expect(signInUrl("/stok?depo=1")).toBe("/giris?next=%2Fstok%3Fdepo%3D1");
    expect(signInUrl("//evil")).toBe("/giris");
    expect(signInUrl()).toBe("/giris");
  });
});
