import { beforeEach, describe, expect, it } from "vitest";
import {
  activeMemberships,
  clearActiveTenantId,
  readActiveTenantId,
  resolveActiveTenant,
  toRole,
  writeActiveTenantId,
  type Membership,
} from "./active-tenant";

const m = (tenantId: string, role = "owner", status = "active"): Membership => ({
  tenantId,
  tenantName: tenantId.toUpperCase(),
  tenantSlug: tenantId,
  role,
  status,
  tenantStatus: "active",
});

beforeEach(() => window.localStorage.clear());

describe("resolveActiveTenant", () => {
  it("sends users without memberships to onboarding", () => {
    expect(resolveActiveTenant([], "a")).toEqual({ kind: "onboarding" });
  });

  it("selects the only membership automatically", () => {
    expect(resolveActiveTenant([m("a")], null)).toEqual({ kind: "active", membership: m("a") });
  });

  it("keeps a stored selection that is still a membership", () => {
    expect(resolveActiveTenant([m("a"), m("b")], "b")).toEqual({ kind: "active", membership: m("b") });
  });

  it("asks to pick when several memberships and no (or a stale) selection", () => {
    expect(resolveActiveTenant([m("a"), m("b")], null).kind).toBe("pick");
    expect(resolveActiveTenant([m("a"), m("b")], "removed").kind).toBe("pick");
  });

  it("ignores a stale selection with a single membership", () => {
    expect(resolveActiveTenant([m("a")], "removed")).toEqual({ kind: "active", membership: m("a") });
  });
});

describe("activeMemberships", () => {
  it("drops invited/disabled memberships", () => {
    expect(activeMemberships({ memberships: [m("a"), m("b", "owner", "disabled"), m("c", "sales", "invited")] })).toEqual([m("a")]);
  });
});

describe("selection storage", () => {
  it("is stored per user", () => {
    writeActiveTenantId("user-1", "a");
    writeActiveTenantId("user-2", "b");
    expect(readActiveTenantId("user-1")).toBe("a");
    expect(readActiveTenantId("user-2")).toBe("b");
    clearActiveTenantId("user-1");
    expect(readActiveTenantId("user-1")).toBeNull();
    expect(readActiveTenantId("user-2")).toBe("b");
  });
});

describe("toRole", () => {
  it("maps known roles and falls back to the most restricted one", () => {
    expect(toRole("warehouse_staff")).toBe("warehouse_staff");
    expect(toRole("dealer_user")).toBe("viewer");
    expect(toRole("")).toBe("viewer");
  });
});
