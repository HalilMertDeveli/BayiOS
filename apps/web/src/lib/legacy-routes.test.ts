import { describe, expect, it } from "vitest";
import { legacyRedirect } from "./legacy-routes";

describe("legacyRedirect", () => {
  it("moves the old product URL to the dynamic segment", () => {
    expect(legacyRedirect("/urunler/duzenle", new URLSearchParams("id=00000000-0000-4000-8000-d00000000001"))).toBe(
      "/urunler/00000000-0000-4000-8000-d00000000001",
    );
  });

  it("encodes the id and falls back to the list without one", () => {
    expect(legacyRedirect("/urunler/duzenle", new URLSearchParams("id=a/b"))).toBe("/urunler/a%2Fb");
    expect(legacyRedirect("/urunler/duzenle", new URLSearchParams())).toBe("/urunler");
    expect(legacyRedirect("/urunler/duzenle/", new URLSearchParams("id="))).toBe("/urunler");
  });

  it("leaves every other path alone", () => {
    expect(legacyRedirect("/urunler", new URLSearchParams("id=x"))).toBeNull();
    expect(legacyRedirect("/urunler/yeni", new URLSearchParams())).toBeNull();
  });
});
