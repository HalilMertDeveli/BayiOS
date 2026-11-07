import { describe, expect, it } from "vitest";
import { fulfillmentMetaFor } from "./labels";

describe("fulfillmentMetaFor (#133)", () => {
  it("an open partial shipment (picking / packed) stays 'Kısmi sevk'", () => {
    expect(fulfillmentMetaFor("partial", "picking").label).toBe("Kısmi sevk");
    expect(fulfillmentMetaFor("partial", "packed").label).toBe("Kısmi sevk");
    expect(fulfillmentMetaFor("partial").label).toBe("Kısmi sevk");
  });

  it("shipped / completed with partial = the rest was closed (close-remaining), not an open partial shipment", () => {
    expect(fulfillmentMetaFor("partial", "shipped")).toEqual({ label: "Kalan kapatıldı", tone: "neutral" });
    expect(fulfillmentMetaFor("partial", "completed")).toEqual({ label: "Kalan kapatıldı", tone: "neutral" });
  });

  it("full and none are unchanged; unknown values are shown as text", () => {
    expect(fulfillmentMetaFor("full", "shipped")).toEqual({ label: "Tamamı sevk edildi", tone: "success" });
    expect(fulfillmentMetaFor("none", "approved").label).toBe("Sevk yok");
    expect(fulfillmentMetaFor("returned", "shipped")).toEqual({ label: "returned", tone: "neutral" });
  });
});
