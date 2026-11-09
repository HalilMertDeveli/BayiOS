import { describe, expect, it } from "vitest";
import type { ApiClient, RequestOptions } from "@/lib/api/client";
import { VARIANT_BATCH_LIMIT, getVariantsBatch, variantBatches } from "./api";

describe("GET /variants/batch", () => {
  it("chunks ids by the API limit (1–100 per call)", () => {
    const ids = Array.from({ length: 205 }, (_, i) => `v${i}`);
    expect(variantBatches(ids).map((c) => c.length)).toEqual([100, 100, 5]);
    expect(VARIANT_BATCH_LIMIT).toBe(100);
  });

  it("sends one tenant-scoped request with comma-separated ids; no call for an empty list", async () => {
    const calls: { path: string; options: RequestOptions }[] = [];
    const api: ApiClient = {
      async request<T>(path: string, options: RequestOptions = {}) {
        calls.push({ path, options });
        return { data: { items: [{ id: "a", productId: "p", sku: "A-1", name: null, productName: "P", barcode: null, status: "active" }] } as T, status: 200, etag: null };
      },
    };
    expect(await getVariantsBatch(api, [])).toEqual([]);
    const items = await getVariantsBatch(api, ["a", "b"]);
    expect(items.map((v) => v.sku)).toEqual(["A-1"]);
    expect(calls).toHaveLength(1);
    expect(calls[0].path).toBe("/api/v1/variants/batch?ids=a%2Cb");
    expect(calls[0].options.tenant).toBe(true);
    await expect(getVariantsBatch(api, Array.from({ length: 101 }, (_, i) => `v${i}`))).rejects.toThrow();
  });
});
