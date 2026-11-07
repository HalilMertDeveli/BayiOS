import { describe, expect, it } from "vitest";
import type { ImportJob } from "./api";
import { canCancel, canRemap, canStart, canValidate, isActive, isResume, rollbackState, stepForStatus, stepState, visibleStep } from "./steps";

type JobState = Pick<ImportJob, "status" | "startedAt" | "validatedAt" | "mapping" | "progress" | "preview" | "rollbackDeadline">;

const job = (patch: Partial<JobState> = {}): JobState => ({
  status: "uploaded",
  startedAt: null,
  validatedAt: null,
  mapping: null,
  progress: { plannedRows: 0, processedRows: 0, percent: 0, created: 0, updated: 0, skipped: 0, failed: 0 },
  preview: { create: 0, update: 0, skip: 0, error: 0 },
  rollbackDeadline: null,
  ...patch,
});

const NOW = Date.parse("2026-11-07T12:00:00Z");

describe("status → step", () => {
  it.each([
    ["uploaded", "mapping"],
    ["mapped", "mapping"],
    ["validating", "preview"],
    ["validated", "preview"],
    ["running", "progress"],
    ["completed", "result"],
    ["failed", "result"],
    ["cancelled", "result"],
    ["rolling_back", "result"],
    ["rolled_back", "result"],
    ["partially_rolled_back", "result"],
    ["rollback_failed", "result"],
  ])("%s → %s", (status, step) => {
    expect(stepForStatus(status)).toBe(step);
  });

  it("only validating, running and rolling_back are polled", () => {
    expect(["validating", "running", "rolling_back"].every(isActive)).toBe(true);
    expect(["validated", "completed", "failed", "cancelled", "rolled_back"].some(isActive)).toBe(false);
  });

  it("back to the mapping only while re-mappable and idle", () => {
    const validated = job({ status: "validated", mapping: { sku: "SKU" }, validatedAt: "x" });
    expect(visibleStep(validated, "mapping")).toBe("mapping");
    expect(visibleStep(validated, null)).toBe("preview");
    expect(visibleStep(job({ status: "validating", mapping: { sku: "SKU" } }), "mapping")).toBe("preview");
    expect(visibleStep(job({ status: "completed", startedAt: "x" }), "mapping")).toBe("result");
  });

  it("stepper state", () => {
    expect(stepState("type", "preview")).toBe("done");
    expect(stepState("preview", "preview")).toBe("current");
    expect(stepState("result", "preview")).toBe("upcoming");
  });
});

describe("transitions (ImportService rules)", () => {
  it("re-mapping stops once anything was imported", () => {
    expect(canRemap(job({ status: "validated" }))).toBe(true);
    expect(canRemap(job({ status: "cancelled", startedAt: "x" }))).toBe(false);
    expect(canRemap(job({ status: "failed", progress: { ...job().progress, processedRows: 3 } }))).toBe(false);
    expect(canRemap(job({ status: "running" }))).toBe(false);
  });

  it("validation needs a saved mapping", () => {
    expect(canValidate(job({ status: "uploaded" }))).toBe(false);
    expect(canValidate(job({ status: "mapped", mapping: { sku: "SKU" } }))).toBe(true);
  });

  it("start after validation with something to write; resume after cancel/failure", () => {
    const preview = { create: 2, update: 1, skip: 0, error: 4 };
    expect(canStart(job({ status: "validated", preview }))).toBe(true);
    expect(canStart(job({ status: "validated", preview: { create: 0, update: 0, skip: 3, error: 2 } }))).toBe(false);
    expect(canStart(job({ status: "cancelled", validatedAt: "x", startedAt: "y", preview }))).toBe(true);
    expect(isResume(job({ status: "cancelled", validatedAt: "x", startedAt: "y" }))).toBe(true);
    expect(canStart(job({ status: "failed", validatedAt: null, preview }))).toBe(false);
    expect(canStart(job({ status: "completed", validatedAt: "x", preview }))).toBe(false);
  });

  it("cancel only before the import finished", () => {
    expect(["uploaded", "mapped", "validating", "validated", "running"].every((status) => canCancel({ status }))).toBe(true);
    expect(["completed", "failed", "cancelled", "rolling_back"].some((status) => canCancel({ status }))).toBe(false);
  });
});

describe("rollback window", () => {
  const imported = { ...job().progress, created: 3, updated: 1 };

  it("available until the deadline, once", () => {
    expect(rollbackState(job({ status: "completed", progress: imported, rollbackDeadline: "2026-11-10T00:00:00Z" }), NOW)).toBe("available");
    expect(rollbackState(job({ status: "completed", progress: imported, rollbackDeadline: "2026-11-01T00:00:00Z" }), NOW)).toBe("expired");
    expect(rollbackState(job({ status: "rolled_back", progress: imported }), NOW)).toBe("none");
  });

  it("nothing imported → nothing to undo; a stalled rollback can be resumed", () => {
    expect(rollbackState(job({ status: "completed", rollbackDeadline: "2026-11-10T00:00:00Z" }), NOW)).toBe("none");
    expect(rollbackState(job({ status: "rollback_failed", progress: imported }), NOW)).toBe("resume");
  });
});
