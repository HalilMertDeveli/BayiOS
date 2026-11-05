import type { Entitlements } from "./entitlements";

const DAY_MS = 24 * 60 * 60 * 1000;

/** The trial banner appears this many days (or fewer) before the trial ends. */
export const TRIAL_BANNER_DAYS = 7;

/**
 * Whole days left until `trialEndsAt`, rounded up ("3 gün kaldı" until the last moment of the third day); 0 once it has
 * passed; null without a date.
 */
export function trialDaysLeft(trialEndsAt: string | null | undefined, now: Date = new Date()): number | null {
  if (!trialEndsAt) return null;
  const end = Date.parse(trialEndsAt);
  if (Number.isNaN(end)) return null;
  return Math.max(0, Math.ceil((end - now.getTime()) / DAY_MS));
}

export type TrialNotice =
  | { kind: "ending"; daysLeft: number; trialEndsAt: string }
  | { kind: "ended"; trialEndsAt: string | null };

/**
 * What the shell banner should say, or null: a trialing tenant whose trial ends within `withinDays`, or a tenant whose
 * subscription expired (it fell back to Temel; no data was deleted).
 */
export function trialNotice(entitlements: Entitlements, now: Date = new Date(), withinDays: number = TRIAL_BANNER_DAYS): TrialNotice | null {
  if (!entitlements.known) return null;
  if (entitlements.status === "expired") return { kind: "ended", trialEndsAt: entitlements.trialEndsAt };
  if (entitlements.status !== "trialing" || !entitlements.trialEndsAt) return null;
  const daysLeft = trialDaysLeft(entitlements.trialEndsAt, now);
  if (daysLeft === null || daysLeft > withinDays) return null;
  return { kind: "ending", daysLeft, trialEndsAt: entitlements.trialEndsAt };
}

/** "bugün" / "1 gün" / "5 gün". */
export function daysLeftText(daysLeft: number): string {
  return daysLeft <= 0 ? "bugün" : `${daysLeft} gün`;
}
