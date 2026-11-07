import { describe, expect, it } from "vitest";
import { isGuestOnlyPath, isPublicPath, safeNextPath, signInUrl } from "./routes";

describe("safeNextPath", () => {
  it.each([
    ["/urunler", "/urunler"],
    ["/urunler?q=vida#ust", "/urunler?q=vida#ust"],
  ])("keeps same-origin path %s", (input, expected) => {
    expect(safeNextPath(input)).toBe(expected);
  });

  it.each([
    null,
    "",
    "https://evil.example",
    "//evil.example/x",
    "/\\evil.example",
    "javascript:alert(1)",
    "urunler",
    // Security audit M-1: inputs that only become protocol-relative after URL normalisation.
    "/.//evil.example/phish",
    "/x/..//evil.example",
    "/./%2e//evil.example",
  ])(
    "rejects %s",
    (input) => {
      expect(safeNextPath(input)).toBe("/pano");
    },
  );
});

describe("route groups", () => {
  it("only the landing, auth screens, callback and the invitation page are public", () => {
    for (const path of ["/", "/giris", "/kayit", "/sifre-sifirla", "/auth/callback", "/davet"]) expect(isPublicPath(path)).toBe(true);
    // The invitation page is reachable signed in too (accepting needs a session).
    expect(isGuestOnlyPath("/davet")).toBe(false);
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
