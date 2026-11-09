import { isBusy } from "./errors";
import { ApiError } from "@/lib/api/client";

/** Wait used when the API's Retry-After is missing or not readable (the API sends 1). */
export const DEFAULT_RETRY_AFTER_SECONDS = 1;
/** Never wait longer than this before the single automatic retry. */
export const MAX_RETRY_AFTER_SECONDS = 5;

export type BusyRetryOptions = {
  /** Injected for tests; defaults to setTimeout. */
  wait?: (ms: number) => Promise<void>;
  /** Called before the retry (e.g. to show "Tekrar deneniyor…"). */
  onRetry?: (seconds: number) => void;
  /** Which errors count as "busy"; defaults to `inventory.busy` (orders also retry `order.busy`). */
  isBusy?: (error: unknown) => boolean;
};

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * `inventory.busy` (409, stock rows locked by another operation, `Retry-After: 1`): waits Retry-After and sends the
 * exact same request once more. `send` must reuse its Idempotency-Key, so a first attempt that did commit is
 * replayed instead of applied twice. A second `busy` is thrown to the caller, which shows a clear message.
 */
export async function withBusyRetry<T>(send: () => Promise<T>, options: BusyRetryOptions = {}): Promise<T> {
  try {
    return await send();
  } catch (error) {
    if (!(options.isBusy ?? isBusy)(error)) throw error;
    const header = error instanceof ApiError ? error.retryAfterSeconds : null;
    const seconds = Math.min(MAX_RETRY_AFTER_SECONDS, Math.max(0, header ?? DEFAULT_RETRY_AFTER_SECONDS));
    options.onRetry?.(seconds);
    await (options.wait ?? sleep)(seconds * 1000);
    return send();
  }
}
