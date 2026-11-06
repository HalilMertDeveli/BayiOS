import { describe, expect, it } from "vitest";
import { accessFor } from "@/test/session";
import { canHandleInvitation, canManageMember, canTransferOwnershipTo, grantableRoles, invitableRoles, roleRank } from "./permissions";

const owner = accessFor("owner");
const admin = accessFor("admin");
const manager = accessFor("manager");
const member = (role: string, userId = `u-${role}`, status = "active") => ({ userId, role, status });

describe("role grantability (MOCK /me fixtures)", () => {
  it("owner invites admin and below, never owner", () => {
    expect(invitableRoles("owner", owner)).toEqual(["admin", "manager", "sales", "warehouse_staff", "viewer"]);
  });

  it("admin invites only manager and below", () => {
    expect(invitableRoles("admin", admin)).toEqual(["manager", "sales", "warehouse_staff", "viewer"]);
  });

  it("roles without members.invite invite nobody", () => {
    expect(invitableRoles("manager", manager)).toEqual([]);
    expect(invitableRoles("sales", accessFor("sales"))).toEqual([]);
    expect(invitableRoles(null, owner)).toEqual([]);
  });

  it("role change options follow the same hierarchy; owner is never offered", () => {
    expect(grantableRoles("owner", owner)).not.toContain("owner");
    expect(grantableRoles("admin", admin)).toEqual(["manager", "sales", "warehouse_staff", "viewer"]);
    expect(grantableRoles("manager", manager)).toEqual([]);
  });

  it("unknown roles rank lowest", () => {
    expect(roleRank("dealer_user")).toBeGreaterThan(roleRank("viewer"));
    expect(invitableRoles("dealer_user", owner)).toEqual([]);
  });
});

describe("who may be managed", () => {
  it("owner manages everyone but themselves", () => {
    expect(canManageMember("owner", "me", owner, member("admin"))).toBe(true);
    expect(canManageMember("owner", "me", owner, member("owner", "u-2"))).toBe(true);
    expect(canManageMember("owner", "me", owner, member("owner", "me"))).toBe(false);
  });

  it("admin manages only members below admin (conditional grant)", () => {
    expect(admin.scope("members.manage")).toBe("conditional");
    expect(canManageMember("admin", "me", admin, member("manager"))).toBe(true);
    expect(canManageMember("admin", "me", admin, member("viewer"))).toBe(true);
    expect(canManageMember("admin", "me", admin, member("admin", "u-9"))).toBe(false);
    expect(canManageMember("admin", "me", admin, member("owner"))).toBe(false);
  });

  it("manager and below manage nobody", () => {
    expect(canManageMember("manager", "me", manager, member("viewer"))).toBe(false);
  });

  it("disabled members can be re-enabled by whoever manages them; invited rows cannot be managed", () => {
    expect(canManageMember("owner", "me", owner, member("sales", "u-1", "disabled"))).toBe(true);
    expect(canManageMember("owner", "me", owner, member("sales", "u-1", "invited"))).toBe(false);
  });
});

describe("ownership transfer and invitations", () => {
  it("only the owner transfers, to another active non-owner member", () => {
    expect(canTransferOwnershipTo("me", owner, member("admin"))).toBe(true);
    expect(canTransferOwnershipTo("me", owner, member("owner", "u-2"))).toBe(false);
    expect(canTransferOwnershipTo("me", owner, member("sales", "u-3", "disabled"))).toBe(false);
    expect(canTransferOwnershipTo("me", owner, member("admin", "me"))).toBe(false);
    expect(canTransferOwnershipTo("me", admin, member("manager"))).toBe(false);
  });

  it("admin cannot resend or revoke admin invitations", () => {
    expect(canHandleInvitation("admin", admin, "admin")).toBe(false);
    expect(canHandleInvitation("admin", admin, "sales")).toBe(true);
    expect(canHandleInvitation("owner", owner, "admin")).toBe(true);
  });
});
