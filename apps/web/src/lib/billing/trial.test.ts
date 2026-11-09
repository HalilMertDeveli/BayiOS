import { describe, expect, it } from "vitest";
import { entitlementsFor } from "@/test/me-fixtures";
import { UNKNOWN_ENTITLEMENTS, createEntitlements } from "./entitlements";
import { TRIAL_BANNER_DAYS, daysLeftText, trialDaysLeft, trialNotice } from "./trial";

const NOW = Date.parse("2026-11-05T09:00:00Z");
const now = new Date(NOW);
const trialEnding = (days: number) => createEntitlements(entitlementsFor("trial", { trialDaysLeft: days, now: NOW }));

describe("trialDaysLeft", () => {
  it("rounds up partial days and never goes below 0", () => {
    expect(trialDaysLeft("2026-11-08T09:00:00Z", now)).toBe(3);
    expect(trialDaysLeft("2026-11-08T10:00:00Z", now)).toBe(4);
    expect(trialDaysLeft("2026-11-05T10:00:00Z", now)).toBe(1);
    expect(trialDaysLeft("2026-11-04T09:00:00Z", now)).toBe(0);
    expect(trialDaysLeft(null, now)).toBeNull();
    expect(trialDaysLeft("not a date", now)).toBeNull();
  });
});

describe("trialNotice", () => {
  it(`shows when a trial ends within ${TRIAL_BANNER_DAYS} days`, () => {
    expect(trialNotice(trialEnding(3), now)).toMatchObject({ kind: "ending", daysLeft: 3 });
    expect(trialNotice(trialEnding(TRIAL_BANNER_DAYS), now)).toMatchObject({ kind: "ending", daysLeft: TRIAL_BANNER_DAYS });
  });

  it("stays hidden for a fresh trial, a paid-equivalent plan and unknown entitlements", () => {
    expect(trialNotice(trialEnding(25), now)).toBeNull();
    expect(trialNotice(createEntitlements(entitlementsFor("professional")), now)).toBeNull();
    expect(trialNotice(createEntitlements(entitlementsFor("basic")), now)).toBeNull();
    expect(trialNotice(UNKNOWN_ENTITLEMENTS, now)).toBeNull();
  });

  it("explains an expired trial (fell back to Temel)", () => {
    expect(trialNotice(createEntitlements(entitlementsFor("expired", { now: NOW })), now)).toMatchObject({ kind: "ended" });
  });

  it("daysLeftText", () => {
    expect(daysLeftText(0)).toBe("bugün");
    expect(daysLeftText(5)).toBe("5 gün");
  });
});
