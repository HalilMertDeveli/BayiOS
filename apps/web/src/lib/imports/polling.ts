/**
 * Status polling for background import jobs (GET /import-jobs/{id}). The delay starts short so a small file feels
 * instant, grows while nothing changes (backoff) and snaps back when progress moves; errors back off harder. A hidden
 * tab does not poll at all: the poller waits for the tab to become visible and then checks at once.
 */
export const POLL_POLICY = { initialMs: 1000, factor: 1.6, maxMs: 8000, errorMaxMs: 30000 } as const;

export type PollPolicy = { initialMs: number; factor: number; maxMs: number; errorMaxMs: number };

export function nextPollDelay(previousMs: number | null, outcome: "changed" | "unchanged" | "error", policy: PollPolicy = POLL_POLICY): number {
  if (previousMs === null || outcome === "changed") return policy.initialMs;
  if (outcome === "error") return Math.min(policy.errorMaxMs, Math.max(policy.initialMs, previousMs) * 2);
  return Math.min(policy.maxMs, Math.round(previousMs * policy.factor));
}

/** What the poller needs of `document`. */
export type VisibilitySource = {
  readonly hidden: boolean;
  addEventListener(type: "visibilitychange", listener: () => void): void;
  removeEventListener(type: "visibilitychange", listener: () => void): void;
};

export type PollerOptions<T> = {
  /** One status read. */
  fetch: (signal: AbortSignal) => Promise<T>;
  /** Called with every successful read. */
  onUpdate: (value: T) => void;
  onError?: (error: unknown) => void;
  /** Keep polling while this holds (e.g. status validating / running / rolling_back). */
  shouldContinue: (value: T) => boolean;
  /** Fingerprint of what the user sees (status + counters); a change resets the backoff. */
  fingerprint: (value: T) => string;
  policy?: PollPolicy;
  /** The page's visibility (defaults to `document`); injectable for tests. */
  doc?: VisibilitySource | null;
  /** Wait before the first read (the caller usually already has a fresh value). */
  initialDelayMs?: number;
};

/** Starts polling; returns stop(). Never overlaps requests; stop() aborts an in-flight one. */
export function startPolling<T>(options: PollerOptions<T>): () => void {
  const policy = options.policy ?? POLL_POLICY;
  const doc = options.doc === undefined ? (typeof document === "undefined" ? null : document) : options.doc;
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let controller: AbortController | null = null;
  let delay: number | null = null;
  let lastPrint: string | null = null;
  let waitingForVisible = false;

  const schedule = (ms: number) => {
    if (stopped) return;
    timer = setTimeout(tick, ms);
  };

  const tick = async () => {
    timer = null;
    if (stopped) return;
    if (doc?.hidden) {
      waitingForVisible = true;
      return;
    }
    controller = new AbortController();
    try {
      const value = await options.fetch(controller.signal);
      if (stopped) return;
      options.onUpdate(value);
      if (!options.shouldContinue(value)) {
        stop();
        return;
      }
      const print = options.fingerprint(value);
      delay = nextPollDelay(delay, lastPrint !== null && print === lastPrint ? "unchanged" : "changed", policy);
      lastPrint = print;
    } catch (error) {
      if (stopped) return;
      options.onError?.(error);
      delay = nextPollDelay(delay ?? policy.initialMs, "error", policy);
    } finally {
      controller = null;
    }
    schedule(delay ?? policy.initialMs);
  };

  const onVisibility = () => {
    if (stopped || doc?.hidden || !waitingForVisible) return;
    waitingForVisible = false;
    delay = null; // fresh start after the tab comes back
    void tick();
  };

  const stop = () => {
    stopped = true;
    if (timer) clearTimeout(timer);
    timer = null;
    controller?.abort();
    doc?.removeEventListener("visibilitychange", onVisibility);
  };

  doc?.addEventListener("visibilitychange", onVisibility);
  schedule(options.initialDelayMs ?? policy.initialMs);
  return stop;
}
