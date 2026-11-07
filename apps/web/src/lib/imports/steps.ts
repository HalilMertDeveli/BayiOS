import { num, type ImportJob } from "./api";

/**
 * Wizard steps. "type" and "upload" happen before a job exists (/ice-aktarma/yeni); the rest are derived from the
 * job's status (ImportStatuses lifecycle: uploaded → mapped → validating → validated → running → completed;
 * failed / cancelled; completed → rolling_back → rolled_back | partially_rolled_back | rollback_failed).
 */
export const WIZARD_STEPS = ["type", "upload", "mapping", "preview", "progress", "result"] as const;
export type WizardStep = (typeof WIZARD_STEPS)[number];

export const stepLabels: Record<WizardStep, string> = {
  type: "Tür",
  upload: "Dosya",
  mapping: "Kolon eşleme",
  preview: "Önizleme",
  progress: "Aktarma",
  result: "Sonuç",
};

/** A background job owns the import: poll until it leaves these states. */
export const ACTIVE_STATUSES: readonly string[] = ["validating", "running", "rolling_back"];

export const isActive = (status: string) => ACTIVE_STATUSES.includes(status);

type JobState = Pick<ImportJob, "status" | "startedAt" | "validatedAt" | "mapping" | "progress" | "preview">;

/** The step that shows a job in its current status. */
export function stepForStatus(status: string): WizardStep {
  switch (status) {
    case "uploaded":
    case "mapped":
      return "mapping";
    case "validating":
    case "validated":
      return "preview";
    case "running":
      return "progress";
    default:
      // completed, failed, cancelled and every rollback state.
      return "result";
  }
}

/** Mapping can change only before anything was imported (ImportService.CanRemap). */
export function canRemap(job: JobState): boolean {
  return (
    job.startedAt === null &&
    num(job.progress.processedRows) === 0 &&
    ["uploaded", "mapped", "validated", "failed", "cancelled"].includes(job.status)
  );
}

/** Dry run: needs a saved mapping and a re-mappable state (not "uploaded"). */
export function canValidate(job: JobState): boolean {
  return job.mapping !== null && job.status !== "uploaded" && canRemap(job);
}

/** Rows the run would write (create + update); errors and skips are never written. */
export const importableRows = (job: Pick<ImportJob, "preview">) => num(job.preview.create) + num(job.preview.update);

/** Start after validation, or resume a cancelled / failed run that was validated (row checkpoint). */
export function canStart(job: JobState): boolean {
  const resumable = (job.status === "cancelled" || job.status === "failed") && job.validatedAt !== null;
  return (job.status === "validated" || resumable) && importableRows(job) > 0;
}

/** True when a start continues an earlier run instead of beginning a new one. */
export const isResume = (job: JobState) => job.status !== "validated" && job.startedAt !== null;

export function canCancel(job: Pick<ImportJob, "status">): boolean {
  return ["uploaded", "mapped", "validating", "validated", "running"].includes(job.status);
}

/**
 * Rollback window: a completed import until `rollbackDeadline` (7 days after it finished), once; a stalled
 * rollback (`rollback_failed`) can be resumed. Nothing imported → nothing to undo.
 */
export function rollbackState(
  job: Pick<ImportJob, "status" | "rollbackDeadline" | "progress">,
  now: number,
): "available" | "resume" | "expired" | "none" {
  if (job.status === "rollback_failed") return "resume";
  if (job.status !== "completed") return "none";
  if (num(job.progress.created) + num(job.progress.updated) === 0) return "none";
  if (!job.rollbackDeadline || Date.parse(job.rollbackDeadline) <= now) return "expired";
  return "available";
}

/**
 * Which step the wizard shows: the status step, unless the user went back to the mapping (only while re-mappable
 * and no background job runs).
 */
export function visibleStep(job: JobState, requested: WizardStep | null): WizardStep {
  const natural = stepForStatus(job.status);
  if (requested === "mapping" && natural !== "mapping" && !isActive(job.status) && canRemap(job)) return "mapping";
  return natural;
}

/** Steps before `step` are done; for the stepper. */
export function stepState(step: WizardStep, current: WizardStep): "done" | "current" | "upcoming" {
  const a = WIZARD_STEPS.indexOf(step);
  const b = WIZARD_STEPS.indexOf(current);
  return a < b ? "done" : a === b ? "current" : "upcoming";
}
