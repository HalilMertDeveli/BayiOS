import { describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api/client";
import { withBusyRetry } from "./busy-retry";

const busy = (retryAfter: number | null = 1) => new ApiError("busy", 409, { code: "inventory.busy" }, undefined, retryAfter);

describe("withBusyRetry", () => {
  it("retries once after Retry-After and returns the second result", async () => {
    const send = vi.fn().mockRejectedValueOnce(busy(2)).mockResolvedValueOnce("ok");
    const wait = vi.fn().mockResolvedValue(undefined);
    const onRetry = vi.fn();
    await expect(withBusyRetry(send, { wait, onRetry })).resolves.toBe("ok");
    expect(send).toHaveBeenCalledTimes(2);
    expect(wait).toHaveBeenCalledWith(2000);
    expect(onRetry).toHaveBeenCalledWith(2);
  });

  it("falls back to 1 s when Retry-After is unreadable and caps long waits", async () => {
    const wait = vi.fn().mockResolvedValue(undefined);
    await withBusyRetry(vi.fn().mockRejectedValueOnce(busy(null)).mockResolvedValueOnce(1), { wait });
    await withBusyRetry(vi.fn().mockRejectedValueOnce(busy(60)).mockResolvedValueOnce(1), { wait });
    expect(wait.mock.calls).toEqual([[1000], [5000]]);
  });

  it("gives up after the single retry and rethrows the second busy", async () => {
    const second = busy();
    const send = vi.fn().mockRejectedValueOnce(busy()).mockRejectedValueOnce(second);
    await expect(withBusyRetry(send, { wait: async () => undefined })).rejects.toBe(second);
    expect(send).toHaveBeenCalledTimes(2);
  });

  it("does not retry other errors", async () => {
    const other = new ApiError("x", 422, { code: "validation.failed" });
    const send = vi.fn().mockRejectedValue(other);
    await expect(withBusyRetry(send, { wait: async () => undefined })).rejects.toBe(other);
    expect(send).toHaveBeenCalledTimes(1);
  });
});
