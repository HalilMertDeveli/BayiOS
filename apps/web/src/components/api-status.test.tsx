import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { ApiStatus } from "./api-status";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("ApiStatus", () => {
  it("shows the API version once connected", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(Response.json({ name: "BayiOS API", version: "0.1.0", environment: "Development" })),
    );

    render(<ApiStatus />);

    expect(screen.getByRole("status").textContent).toContain("kontrol ediliyor");
    expect(await screen.findByText(/API bağlı: BayiOS API 0\.1\.0/)).toBeDefined();
  });

  it("shows a friendly error when the API is unreachable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("network")));

    render(<ApiStatus />);

    expect(await screen.findByText(/API'ye ulaşılamadı/)).toBeDefined();
  });
});
