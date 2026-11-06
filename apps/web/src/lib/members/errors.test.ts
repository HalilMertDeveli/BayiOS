import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { acceptFailure, isStaleMemberState, memberErrorMessage, pendingInvitationId } from "./errors";

const err = (status: number, code: string, extra: Record<string, unknown> = {}) => new ApiError("x", status, { status, code, ...extra });

describe("memberErrorMessage", () => {
  it.each([
    [409, "members.last_owner", "en az bir aktif sahibi"],
    [409, "members.cannot_disable_self", "Kendi üyeliğinizi"],
    [403, "members.not_manageable", "Bu kişiyi yönetemezsiniz"],
    [409, "members.already_member", "zaten işletmenizin bir üyesine"],
    [409, "invitation.already_pending", "bekleyen bir davet zaten var"],
    [409, "invitation.not_pending", "artık beklemede değil"],
  ])("%i %s → Turkish", (status, code, text) => {
    expect(memberErrorMessage(err(status, code))).toContain(text);
  });

  it("names the role that cannot be granted", () => {
    expect(memberErrorMessage(err(403, "members.role_not_grantable", { role: "admin" }))).toMatch(/^“Yönetici” rolünü veremezsiniz\./);
  });

  it("max_users limit names the limit and the numbers", () => {
    const message = memberErrorMessage(err(403, "entitlement.limit_exceeded", { feature: "max_users", limit: 3, current: 3 }));
    expect(message).toContain("Kullanıcı sayısı sınırına ulaşıldı (3/3)");
  });

  it("non-API errors get a generic message", () => {
    expect(memberErrorMessage(new Error("boom"))).toContain("Beklenmeyen");
  });

  it("reads the pending invitation id and stale states", () => {
    expect(pendingInvitationId(err(409, "invitation.already_pending", { invitationId: "i-7" }))).toBe("i-7");
    expect(pendingInvitationId(err(409, "members.already_member"))).toBeNull();
    expect(isStaleMemberState(err(404, "invitation.not_found"))).toBe(true);
    expect(isStaleMemberState(err(409, "members.last_owner"))).toBe(false);
  });
});

describe("acceptFailure", () => {
  it("email mismatch: switch account, keep the token", () => {
    const f = acceptFailure(err(403, "invitation.email_mismatch"));
    expect(f).toMatchObject({ title: "Bu davet başka bir e-posta adresi için", next: "switch-account", final: false });
  });

  it("unverified e-mail: verify then retry", () => {
    expect(acceptFailure(err(403, "invitation.email_not_verified"))).toMatchObject({ next: "verify", final: false });
  });

  it.each([
    [410, "invitation.expired", "Davetin süresi dolmuş"],
    [404, "invitation.not_found", "Davet geçersiz"],
  ])("%i %s is final (token dropped)", (status, code, title) => {
    expect(acceptFailure(err(status, code))).toMatchObject({ title, next: "none", final: true });
  });

  it("already a member: go to the app", () => {
    expect(acceptFailure(err(409, "invitation.already_member"))).toMatchObject({ next: "go-app", final: true });
  });

  it("inactive tenant and seat limit are not final", () => {
    expect(acceptFailure(err(403, "tenant.inactive"))).toMatchObject({ title: "İşletme kullanılamıyor", final: false });
    expect(acceptFailure(err(403, "entitlement.limit_exceeded"))).toMatchObject({ title: "İşletmenin kullanıcı sınırı dolu", next: "retry", final: false });
  });

  it("network / unknown errors can be retried", () => {
    expect(acceptFailure(new ApiError("x", 0, null, "Sunucuya ulaşılamadı."))).toMatchObject({ message: "Sunucuya ulaşılamadı.", next: "retry" });
    expect(acceptFailure("boom")).toMatchObject({ next: "retry" });
  });
});
