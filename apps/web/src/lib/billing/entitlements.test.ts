import { describe, expect, it } from "vitest";
import { entitlementsFor } from "@/test/me-fixtures";
import { FEATURES, LIMITS, UNKNOWN_ENTITLEMENTS, createEntitlements, featureGate, limitGate, warehouseGate } from "./entitlements";

const basic = createEntitlements(entitlementsFor("basic"));
const trial = createEntitlements(entitlementsFor("trial"));
const professional = createEntitlements(entitlementsFor("professional"));

const active = { status: "active" };
const inactive = { status: "inactive" };

describe("createEntitlements (MOCK /me entitlements)", () => {
  it("reads features and limits; null = unlimited, an absent limit = 0 (fail-closed like the API)", () => {
    expect(trial.known).toBe(true);
    expect(trial.has(FEATURES.aiOrderParsing)).toBe(true);
    expect(trial.limit(LIMITS.maxWarehouses)).toBeNull();
    expect(basic.has(FEATURES.warehouseLocations)).toBe(false);
    // OD-02 (2026-11-09): Basic 999 users, warehouses and products.
    expect(basic.limit(LIMITS.maxWarehouses)).toBe(999);
    expect(basic.limit(LIMITS.maxUsers)).toBe(999);
    expect(basic.limit(LIMITS.maxProducts)).toBe(999);
    expect(trial.limit(LIMITS.maxProducts)).toBeNull();
    expect(basic.limit(LIMITS.aiParsesPerMonth)).toBe(0);
  });

  it("accepts limits sent as numeric strings", () => {
    const e = createEntitlements({ ...entitlementsFor("basic"), limits: [{ code: "max_warehouses", value: "3" }] });
    expect(e.limit("max_warehouses")).toBe(3);
  });

  it("without data nothing is locked (the API decides)", () => {
    expect(createEntitlements(null)).toBe(UNKNOWN_ENTITLEMENTS);
    expect(featureGate(UNKNOWN_ENTITLEMENTS, FEATURES.warehouseLocations)).toEqual({ locked: false });
    expect(warehouseGate(UNKNOWN_ENTITLEMENTS, [active, active, active])).toEqual({ locked: false });
  });
});

describe("gates", () => {
  it("locations are a Profesyonel feature", () => {
    expect(featureGate(professional, FEATURES.warehouseLocations)).toEqual({ locked: false });
    expect(featureGate(basic, FEATURES.warehouseLocations)).toEqual({
      locked: true,
      kind: "feature",
      code: "warehouse_locations",
      suggestedPlan: "professional",
      suggestedModule: null,
    });
  });

  it("the AI assistant is an add-on module", () => {
    expect(featureGate(professional, FEATURES.aiOrderParsing)).toMatchObject({ locked: true, suggestedPlan: null, suggestedModule: "ai_assistant" });
    expect(featureGate(trial, FEATURES.aiOrderParsing)).toEqual({ locked: false });
  });

  it("at the warehouse limit a further active warehouse is locked; inactive ones do not count", () => {
    // Basic allows 999 (OD-02); an operator limit of 1 keeps the case small.
    const basic = createEntitlements({ ...entitlementsFor("basic"), limits: [{ code: "max_warehouses", value: 1 }] });
    expect(warehouseGate(createEntitlements(entitlementsFor("basic")), [active, active, active])).toEqual({ locked: false });
    expect(warehouseGate(basic, [])).toEqual({ locked: false });
    expect(warehouseGate(basic, [inactive, inactive])).toEqual({ locked: false });
    expect(warehouseGate(basic, [active, inactive])).toEqual({
      locked: true,
      kind: "limit",
      code: "max_warehouses",
      limit: 1,
      current: 1,
      suggestedPlan: "professional",
      suggestedModule: null,
    });
    expect(warehouseGate(trial, [active, active, active])).toEqual({ locked: false });
  });

  it("limitGate compares current + 1 against the limit", () => {
    const e = createEntitlements({ ...entitlementsFor("basic"), limits: [{ code: "max_users", value: 5 }] });
    expect(limitGate(e, "max_users", 4)).toEqual({ locked: false });
    expect(limitGate(e, "max_users", 5)).toMatchObject({ locked: true, limit: 5, current: 5 });
  });
});
