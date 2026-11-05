import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ToastProvider } from "@/components/ui/toast";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import { twoShelfTask } from "@/lib/fulfilment/test-fixtures";
import type { Role } from "@/lib/navigation";
import { SessionContext, type SessionContextValue } from "@/lib/session/session-context";
import { PickScreen } from "./pick-screen";

vi.mock("next/navigation", () => ({ usePathname: () => "/depo/gorevler/t1", useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));

type Call = { path: string; options: RequestOptions };

function fakeApi(scan: (body: Record<string, unknown>) => unknown) {
  const calls: Call[] = [];
  const api: ApiClient = {
    async request<T>(path: string, options: RequestOptions = {}) {
      calls.push({ path, options });
      if (path === "/api/v1/picking-tasks/t1") return { data: twoShelfTask() as T, status: 200, etag: 'W/"1"' };
      if (path === "/api/v1/picking-tasks/t1/scans") return { data: scan(options.body as Record<string, unknown>) as T, status: 200, etag: 'W/"2"' };
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  return { api, calls };
}

function renderScreen(api: ApiClient, role: Role = "warehouse_staff") {
  const value: SessionContextValue = {
    state: { status: "ready", user: { id: "u1", email: null, fullName: null }, memberships: [], active: null },
    api,
    role,
    selectTenant: vi.fn(),
    reload: vi.fn(),
    signOut: vi.fn(),
  };
  return render(
    <SessionContext value={value}>
      <ToastProvider>
        <PickScreen taskId="t1" />
      </ToastProvider>
    </SessionContext>,
  );
}

const progress = (picked: number) => ({
  taskId: "t1",
  status: "in_progress",
  version: 2,
  openLineCount: 2,
  lines: [{ id: "l1", variantId: "v1", sku: "MOCK-001", locationId: "loc-a", quantityToPick: 3, quantityPicked: picked, quantityShort: 0, quantityRemaining: 3 - picked }],
});

describe("PickScreen scan flow", () => {
  it("shelf first, then item: sends the scanned shelf with quantity 1 and a fresh key per scan", async () => {
    let picked = 0;
    const { api, calls } = fakeApi((body) => {
      picked += Number(body.quantity);
      return progress(picked);
    });
    const user = userEvent.setup();
    renderScreen(api);

    expect(await screen.findByTestId("pick-step")).toHaveProperty("textContent", "1. Rafı okutun: A-01");
    const input = screen.getByLabelText("Raf barkodu");
    await waitFor(() => expect(document.activeElement).toBe(input));
    await user.type(input, "LOC-A01{Enter}");
    expect(screen.getByTestId("pick-step").textContent).toBe("2. Ürünü okutun: MOCK-001");
    expect(screen.getByTestId("scan-feedback").textContent).toContain("Raf okutuldu: A-01");

    await user.type(screen.getByLabelText("Ürün barkodu veya SKU"), "8690000000011{Enter}");
    await waitFor(() => expect(screen.getByTestId("scan-feedback").dataset.tone).toBe("success"));
    expect(screen.getByTestId("scan-feedback").textContent).toContain("MOCK-001 +1");
    expect(screen.getByTestId("current-remaining").textContent).toContain("Kalan 2");

    // Scanning again counts +1 again (new attempt → new Idempotency-Key, same body).
    await user.type(screen.getByLabelText("Ürün barkodu veya SKU"), "8690000000011{Enter}");
    await waitFor(() => expect(screen.getByTestId("current-remaining").textContent).toContain("Kalan 1"));
    const scans = calls.filter((c) => c.path.endsWith("/scans"));
    expect(scans.map((c) => c.options.body)).toEqual([
      { code: "8690000000011", location: "LOC-A01", quantity: 1, lineId: null },
      { code: "8690000000011", location: "LOC-A01", quantity: 1, lineId: null },
    ]);
    expect(scans[0].options.idempotencyKey).toBeTruthy();
    expect(scans[0].options.idempotencyKey).not.toBe(scans[1].options.idempotencyKey);
    expect(scans[0].options.ifMatch).toBeUndefined();
  });

  it("the stepper sets the quantity of the next scan and resets to 1 afterwards", async () => {
    const { api, calls } = fakeApi((body) => progress(Number(body.quantity)));
    const user = userEvent.setup();
    renderScreen(api);
    await user.type(await screen.findByLabelText("Raf barkodu"), "A-01{Enter}");
    await user.click(screen.getByRole("button", { name: "Adet artır" }));
    await user.click(screen.getByRole("button", { name: "Adet artır" }));
    await user.click(screen.getByRole("button", { name: "Adet artır" })); // clamped to the 3 still open
    expect(screen.getByTestId("step-value").textContent).toBe("3");
    await user.type(screen.getByLabelText("Ürün barkodu veya SKU"), "MOCK-001{Enter}");
    await waitFor(() => expect(screen.getByTestId("scan-feedback").dataset.tone).toBe("success"));
    expect(calls.find((c) => c.path.endsWith("/scans"))?.options.body).toMatchObject({ quantity: 3, location: "A-01" });
    expect(screen.getByTestId("step-value").textContent).toBe("1");
  });

  it("a wrong item is shown as an error and nothing is lost (shelf stays, progress unchanged)", async () => {
    const { api } = fakeApi(() => {
      throw new ApiError("x", 422, { code: "picking.wrong_item" });
    });
    const user = userEvent.setup();
    renderScreen(api);
    await user.type(await screen.findByLabelText("Raf barkodu"), "LOC-A01{Enter}");
    await user.type(screen.getByLabelText("Ürün barkodu veya SKU"), "MOCK-002{Enter}");
    const feedback = screen.getByTestId("scan-feedback");
    await waitFor(() => expect(feedback.dataset.tone).toBe("error"));
    expect(feedback.textContent).toContain("Yanlış ürün");
    expect(feedback.getAttribute("role")).toBe("alert");
    expect(screen.getByTestId("pick-step").textContent).toBe("2. Ürünü okutun: MOCK-001");
    expect(screen.getByTestId("current-remaining").textContent).toContain("Kalan 3");
  });

  it("a wrong shelf is forgotten so the worker rescans it", async () => {
    const { api } = fakeApi(() => {
      throw new ApiError("x", 422, { code: "picking.wrong_location" });
    });
    const user = userEvent.setup();
    renderScreen(api);
    await user.type(await screen.findByLabelText("Raf barkodu"), "LOC-Z99{Enter}");
    await user.type(screen.getByLabelText("Ürün barkodu veya SKU"), "MOCK-001{Enter}");
    await waitFor(() => expect(screen.getByTestId("scan-feedback").textContent).toContain("Yanlış raf"));
    expect(screen.getByTestId("pick-step").textContent).toBe("1. Rafı okutun: A-01");
  });

  it("a lost answer offers 'Tekrar gönder', which resends the same scan with the same key", async () => {
    let first = true;
    const { api, calls } = fakeApi(() => {
      if (first) {
        first = false;
        throw new ApiError("x", 0, null, "Sunucuya ulaşılamadı.");
      }
      return progress(1);
    });
    const user = userEvent.setup();
    renderScreen(api);
    await user.type(await screen.findByLabelText("Raf barkodu"), "LOC-A01{Enter}");
    await user.type(screen.getByLabelText("Ürün barkodu veya SKU"), "MOCK-001{Enter}");
    await user.click(await screen.findByRole("button", { name: "Tekrar gönder" }));
    await waitFor(() => expect(screen.getByTestId("scan-feedback").dataset.tone).toBe("success"));
    const scans = calls.filter((c) => c.path.endsWith("/scans"));
    expect(scans).toHaveLength(2);
    expect(scans[1].options.idempotencyKey).toBe(scans[0].options.idempotencyKey);
    expect(scans[1].options.body).toEqual(scans[0].options.body);
  });

  it("a viewer cannot open the pick screen", () => {
    const { api, calls } = fakeApi(() => progress(1));
    renderScreen(api, "viewer");
    expect(screen.getByText("Görev ayrıntısını görme yetkiniz yok")).toBeTruthy();
    expect(calls).toHaveLength(0);
  });
});
