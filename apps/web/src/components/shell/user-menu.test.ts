import { describe, expect, it } from "vitest";
import { initials } from "./user-menu";

describe("initials (header avatar)", () => {
  it("takes the first and last name, with Turkish upper-casing", () => {
    expect(initials("Ayşe Yılmaz")).toBe("AY");
    expect(initials("ismail Can İnce")).toBe("İİ");
    expect(initials("Mert")).toBe("M");
  });

  it("skips words that do not start with a letter or digit", () => {
    expect(initials("Örnek Müdür (sentetik)")).toBe("ÖM");
    expect(initials("Demo Toptan Sahibi")).toBe("DS");
    expect(initials("(sentetik)")).toBe("(");
  });

  it("uses the first letter of an e-mail address", () => {
    expect(initials("mock.kullanici@example.test")).toBe("M");
  });
});
