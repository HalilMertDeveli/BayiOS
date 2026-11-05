import { beforeEach, describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createEntitlements } from "@/lib/billing/entitlements";
import { SessionContext, type SessionContextValue } from "@/lib/session/session-context";
import { entitlementsFor, type FixtureRole } from "@/test/me-fixtures";
import { mockSession } from "@/test/session";
import { TrialBanner } from "./trial-banner";

/** MOCK session whose /me entitlements end the trial in `days` days. */
function sessionWith(role: FixtureRole, raw: ReturnType<typeof entitlementsFor>): SessionContextValue {
  return mockSession(role, { entitlements: createEntitlements(raw) });
}

const renderBanner = (value: SessionContextValue) =>
  render(
    <SessionContext value={value}>
      <TrialBanner />
    </SessionContext>,
  );

beforeEach(() => window.sessionStorage.clear());

describe("TrialBanner", () => {
  it("is hidden while the trial has more than a week left", () => {
    renderBanner(sessionWith("owner", entitlementsFor("trial", { trialDaysLeft: 20 })));
    expect(screen.queryByRole("region", { name: "Deneme süresi" })).toBeNull();
  });

  it("warns the owner a few days before the end, says no payment is taken, links to the plan page", () => {
    renderBanner(sessionWith("owner", entitlementsFor("trial", { trialDaysLeft: 3 })));
    const banner = screen.getByRole("region", { name: "Deneme süresi" });
    expect(banner.textContent).toContain("3 gün sonra bitiyor");
    expect(banner.textContent).toContain("Ödeme alınmaz");
    expect(banner.textContent).toContain("verileriniz silinmez");
    expect(screen.getByRole("link", { name: "Paket ve modüller" }).getAttribute("href")).toBe("/ayarlar/paket");
  });

  it("roles without billing.read see the notice without the link", () => {
    renderBanner(sessionWith("sales", entitlementsFor("trial", { trialDaysLeft: 2 })));
    expect(screen.getByRole("region", { name: "Deneme süresi" })).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Paket ve modüller" })).toBeNull();
  });

  it("can be dismissed for the session", async () => {
    const value = sessionWith("owner", entitlementsFor("trial", { trialDaysLeft: 1 }));
    const { unmount } = renderBanner(value);
    await userEvent.click(screen.getByRole("button", { name: "Deneme bildirimini kapat" }));
    expect(screen.queryByRole("region", { name: "Deneme süresi" })).toBeNull();
    unmount();
    renderBanner(value);
    expect(screen.queryByRole("region", { name: "Deneme süresi" })).toBeNull();
  });

  it("explains an expired trial: Temel, nothing deleted", () => {
    renderBanner(sessionWith("owner", entitlementsFor("expired")));
    const banner = screen.getByRole("region", { name: "Deneme süresi" });
    expect(banner.textContent).toContain("sona erdi");
    expect(banner.textContent).toContain("Temel pakette");
    expect(banner.textContent).toContain("Verileriniz silinmedi");
  });
});
