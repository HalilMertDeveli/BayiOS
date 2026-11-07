import { describe, expect, it } from "vitest";
import type { ApiClient } from "@/lib/api/client";
import { DEFAULT_MESSAGE_LIMITS, ParseStillPendingError, messageLimits, waitForOrderParse, type AiSettings } from "./api";
import { messageSegments } from "./review";

/** MOCK status endpoint: answers the given states in order (the last one repeats). Synthetic data only. */
function statusApi(states: string[]) {
  const calls: string[] = [];
  const api: ApiClient = {
    async request<T>(path: string) {
      calls.push(path);
      const state = states.length > 1 ? states.shift()! : states[0];
      return { data: { id: "p-1", state, parseStatus: state, failureCode: null, resultUrl: "/x", pollAfterMs: 100 } as T, status: 200, etag: null };
    },
  };
  return { api, calls };
}

describe("waitForOrderParse", () => {
  it("polls the status URL with exponential backoff up to the cap until the parse is done", async () => {
    const { api, calls } = statusApi(["pending", "pending", "pending", "pending", "succeeded"]);
    const delays: number[] = [];
    const status = await waitForOrderParse(api, "p-1", {
      initialDelayMs: 750,
      maxDelayMs: 2000,
      sleep: async (ms) => {
        delays.push(ms);
      },
    });
    expect(status.state).toBe("succeeded");
    expect(calls.every((c) => c === "/api/v1/ai/order-parses/p-1/status")).toBe(true);
    expect(delays).toEqual([750, 1125, 1687.5, 2000, 2000]);
  });

  it("returns a failed state (the caller explains it) and gives up after the timeout while still pending", async () => {
    expect((await waitForOrderParse(statusApi(["failed"]).api, "p-1", { sleep: async () => {} })).state).toBe("failed");

    let clock = 0;
    const pending = waitForOrderParse(statusApi(["pending"]).api, "p-1", {
      timeoutMs: 5000,
      now: () => clock,
      sleep: async (ms) => {
        clock += ms;
      },
    });
    await expect(pending).rejects.toBeInstanceOf(ParseStillPendingError);
  });

  it("stops when aborted", async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(waitForOrderParse(statusApi(["pending"]).api, "p-1", { signal: controller.signal })).rejects.toBeTruthy();
  });
});

describe("messageLimits", () => {
  it("uses the server's limits when published, the defaults otherwise", () => {
    const settings = { limits: { maxInputChars: 2000, maxInputLines: 80, hourlyParseLimit: 30 } } as unknown as AiSettings;
    expect(messageLimits(settings)).toEqual({ maxChars: 2000, maxLines: 80 });
    expect(messageLimits(null)).toEqual(DEFAULT_MESSAGE_LIMITS);
  });
});

describe("messageSegments with API offsets", () => {
  it("marks exactly the API's span (even when spacing differs from rawText) and falls back to text search", () => {
    const message = "Selam\n10 adet  H4 ampul\n3 takım balata";
    const segments = messageSegments(message, [
      { lineNo: 1, rawText: "10 adet h4 ampul", textStart: 6, textLength: 17 },
      { lineNo: 2, rawText: "3 takım balata", textStart: null, textLength: null },
    ]);
    expect(segments.filter((s) => s.lineNo !== null).map((s) => [s.lineNo, s.text])).toEqual([
      [1, "10 adet  H4 ampul"],
      [2, "3 takım balata"],
    ]);
    expect(segments.map((s) => s.text).join("")).toBe(message);
  });

  it("ignores offsets outside the message", () => {
    const segments = messageSegments("2 adet buji", [{ lineNo: 1, rawText: "2 adet buji", textStart: 5, textLength: 40 }]);
    expect(segments).toEqual([{ text: "2 adet buji", lineNo: 1 }]);
  });
});
