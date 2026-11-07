import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { POLL_POLICY, nextPollDelay, startPolling } from "./polling";

describe("nextPollDelay", () => {
  it("starts at the initial delay and resets when something changed", () => {
    expect(nextPollDelay(null, "unchanged")).toBe(POLL_POLICY.initialMs);
    expect(nextPollDelay(6000, "changed")).toBe(POLL_POLICY.initialMs);
  });

  it("backs off while nothing changes, capped", () => {
    let d: number = POLL_POLICY.initialMs;
    const seen: number[] = [];
    for (let i = 0; i < 8; i++) seen.push((d = nextPollDelay(d, "unchanged")));
    expect(seen[0]).toBe(1600);
    expect(seen.every((x, i) => i === 0 || x >= seen[i - 1])).toBe(true);
    expect(seen.at(-1)).toBe(POLL_POLICY.maxMs);
  });

  it("errors back off harder, up to the error cap", () => {
    expect(nextPollDelay(1000, "error")).toBe(2000);
    expect(nextPollDelay(20000, "error")).toBe(POLL_POLICY.errorMaxMs);
  });
});

/** Minimal document stand-in: `hidden` + visibilitychange listeners. */
function fakeDocument() {
  const listeners = new Set<() => void>();
  return {
    hidden: false,
    addEventListener: (_: string, fn: () => void) => void listeners.add(fn),
    removeEventListener: (_: string, fn: () => void) => void listeners.delete(fn),
    fire() {
      for (const fn of listeners) fn();
    },
    listeners,
  };
}

describe("startPolling", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("polls until the job settles, then stops", async () => {
    const statuses = ["running", "running", "completed"];
    const fetch = vi.fn(async () => statuses.shift() ?? "completed");
    const onUpdate = vi.fn();
    const doc = fakeDocument();
    startPolling({ fetch, onUpdate, shouldContinue: (s) => s === "running", fingerprint: (s) => s, doc, policy: { ...POLL_POLICY } });

    await vi.advanceTimersByTimeAsync(1000);
    expect(fetch).toHaveBeenCalledTimes(1);
    // Unchanged → backoff 1.6 s.
    await vi.advanceTimersByTimeAsync(1000);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1600);
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(onUpdate).toHaveBeenLastCalledWith("completed");
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(doc.listeners.size).toBe(0);
  });

  it("does not poll in a hidden tab and checks at once when it becomes visible", async () => {
    const fetch = vi.fn(async () => "running");
    const doc = fakeDocument();
    doc.hidden = true;
    const stop = startPolling({ fetch, onUpdate: () => {}, shouldContinue: () => true, fingerprint: (s) => s, doc });

    await vi.advanceTimersByTimeAsync(30_000);
    expect(fetch).not.toHaveBeenCalled();
    doc.hidden = false;
    doc.fire();
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1);
    stop();
  });

  it("keeps going after an error with a longer delay and reports it", async () => {
    let calls = 0;
    const fetch = vi.fn(async () => {
      calls += 1;
      if (calls === 1) throw new Error("offline");
      return "running";
    });
    const onError = vi.fn();
    const stop = startPolling({ fetch, onUpdate: () => {}, onError, shouldContinue: () => true, fingerprint: (s) => s, doc: fakeDocument() });

    await vi.advanceTimersByTimeAsync(1000);
    expect(onError).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1999);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(fetch).toHaveBeenCalledTimes(2);
    stop();
  });

  it("stop() aborts the request in flight and nothing is reported afterwards", async () => {
    let signal: AbortSignal | null = null;
    const onUpdate = vi.fn();
    const stop = startPolling({
      fetch: (s) => {
        signal = s;
        return new Promise<string>((resolve) => setTimeout(() => resolve("running"), 5000));
      },
      onUpdate,
      shouldContinue: () => true,
      fingerprint: (s) => s,
      doc: fakeDocument(),
    });
    await vi.advanceTimersByTimeAsync(1000);
    stop();
    expect((signal as AbortSignal | null)?.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(onUpdate).not.toHaveBeenCalled();
  });
});
