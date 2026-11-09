import { describe, expect, it } from "vitest";
import { initials } from "./user-menu";

describe("initials (header avatar)", () => {
  it("takes the first and last name, with Turkish upper-casing", () => {
    expect(initials("Ayşe Yılmaz")).toBe("AY");
    expect(initials("ismail Can İnce")).toBe("İİ");
    expect(initials("Mert")).toBe("M");
  });

  it("uses the first letter of an e-mail address", () => {
    expect(initials("mock.kullanici@example.test")).toBe("M");
  });
});
