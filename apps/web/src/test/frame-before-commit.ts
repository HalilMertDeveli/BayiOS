import { vi } from "vitest";

/**
 * Models a slow device (#152): an animation frame runs before React commits a state update made after an `await`.
 * With this in place, `setError(…); requestAnimationFrame(() => ref.current?.focus())` focuses nothing (the alert is
 * not mounted yet); focus that waits for the commit still lands. Restore with `vi.restoreAllMocks()`.
 */
export function frameBeforeCommit() {
  return vi.spyOn(window, "requestAnimationFrame").mockImplementation((callback: FrameRequestCallback) => {
    callback(performance.now());
    return 0;
  });
}
