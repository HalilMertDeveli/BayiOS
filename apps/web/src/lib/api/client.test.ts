import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, getMeta } from "./client";

afterEach(() => vi.unstubAllGlobals());

describe("getMeta", () => {
  it("calls the versioned meta endpoint without a double slash", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      Response.json({ name: "BayiOS API", version: "0.1.0", environment: "Development" }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const meta = await getMeta();

    expect(fetchMock).toHaveBeenCalledWith("http://api.test/api/v1/meta", expect.any(Object));
    expect(meta.name).toBe("BayiOS API");
  });

  it("throws ApiError with the status code on failure", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 503 })));

    await expect(getMeta()).rejects.toMatchObject({ name: "ApiError", status: 503 } satisfies Partial<ApiError>);
  });
});
