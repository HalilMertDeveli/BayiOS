import { describe, expect, it } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import type { Unit } from "@/lib/catalog/api";
import { product, variant } from "@/lib/catalog/test-fixtures";
import { MinimumCell } from "@/components/inventory/quantity-cells";
import { ToastProvider } from "@/components/ui/toast";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { StockThresholdsSection } from "./stock-thresholds-section";

const units: Unit[] = [
  { id: "u1", code: "ADET", name: "Adet", allowsFraction: false, archivedAt: null, version: 1 } as unknown as Unit,
  { id: "u2", code: "KG", name: "Kilogram", allowsFraction: true, archivedAt: null, version: 1 } as unknown as Unit,
];

/** MOCK API for GET /inventory/thresholds and POST /inventory/thresholds/bulk; records every call. */
function fakeApi(options: { failBulk?: ApiError } = {}) {
  const calls: { path: string; options?: RequestOptions }[] = [];
  const api: ApiClient = {
    async request<T>(path: string, requestOptions?: RequestOptions) {
      calls.push({ path, options: requestOptions });
      if (path.startsWith("/api/v1/inventory/thresholds/bulk")) {
        if (options.failBulk) throw options.failBulk;
        return { data: { created: 1, updated: 0, removed: 0, unchanged: 0, items: [] } as T, status: 200, etag: null };
      }
      if (path.startsWith("/api/v1/inventory/thresholds?")) {
        const items = [
          { id: "t1", variantId: "v1", warehouseId: null, minQuantity: 5, version: 1, createdAt: "2026-11-07T10:00:00Z", updatedAt: null },
          { id: "t2", variantId: "v1", warehouseId: "w1", minQuantity: 2, version: 1, createdAt: "2026-11-07T10:00:00Z", updatedAt: null },
        ];
        return { data: { items, page: { nextCursor: null, hasMore: false, limit: 200 } } as T, status: 200, etag: null };
      }
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  return { api, calls };
}

const twoVariants = product({
  variants: [variant({ id: "v1", sku: "FRN-1" }), variant({ id: "v2", sku: "YAG-1", unitId: "u2", name: "1 kg" })],
});

function renderSection(role: "owner" | "viewer", api: ApiClient, readOnly = role === "viewer") {
  render(
    <SessionContext value={mockSession(role, { api })}>
      <ToastProvider>
        <StockThresholdsSection product={twoVariants} units={units} readOnly={readOnly} />
      </ToastProvider>
    </SessionContext>,
  );
}

describe("StockThresholdsSection — product editor minimum stock (MOCK API)", () => {
  it("shows each variant's general minimum and the count of warehouse overrides", async () => {
    const { api, calls } = fakeApi();
    renderSection("owner", api);
    const first = await screen.findByRole("textbox", { name: "FRN-1 minimum stok" });
    await waitFor(() => expect((first as HTMLInputElement).value).toBe("5"));
    expect((screen.getByRole("textbox", { name: "YAG-1 minimum stok" }) as HTMLInputElement).value).toBe("");
    expect(screen.getByText(/1 depoya özel eşik/)).toBeTruthy();
    expect(new URLSearchParams(calls[0].path.split("?")[1]).get("variantIds")).toBe("v1,v2");
  });

  it("saves only the changed rows in one bulk request with an Idempotency-Key", async () => {
    const { api, calls } = fakeApi();
    renderSection("owner", api);
    const first = await screen.findByRole("textbox", { name: "FRN-1 minimum stok" });
    await waitFor(() => expect((first as HTMLInputElement).value).toBe("5"));
    await userEvent.clear(first);
    await userEvent.type(screen.getByRole("textbox", { name: "YAG-1 minimum stok" }), "2,5");
    await userEvent.click(screen.getByRole("button", { name: "Minimum stoku kaydet" }));
    await waitFor(() => expect(calls.some((c) => c.path.endsWith("/bulk"))).toBe(true));
    const bulk = calls.find((c) => c.path.endsWith("/bulk"))!;
    expect(bulk.options?.method).toBe("POST");
    expect(bulk.options?.idempotencyKey).toBeTruthy();
    expect(bulk.options?.body).toEqual({
      items: [
        { variantId: "v1", warehouseId: null, minQuantity: null, remove: true },
        { variantId: "v2", warehouseId: null, minQuantity: "2.5", remove: null },
      ],
    });
  });

  it("rejects a fraction for a whole-number unit without calling the API", async () => {
    const { api, calls } = fakeApi();
    renderSection("owner", api);
    const first = await screen.findByRole("textbox", { name: "FRN-1 minimum stok" });
    await waitFor(() => expect((first as HTMLInputElement).value).toBe("5"));
    await userEvent.clear(first);
    await userEvent.type(first, "1,5");
    await userEvent.click(screen.getByRole("button", { name: "Minimum stoku kaydet" }));
    expect(await screen.findByText(/kesirli olamaz/)).toBeTruthy();
    expect(calls.some((c) => c.path.endsWith("/bulk"))).toBe(false);
  });

  it("maps a 422 row error back to its input", async () => {
    const failBulk = new ApiError("Validation failed", 422, { code: "validation.failed", errors: { "items[0].minQuantity": ["The variant's unit does not allow fractions."] } });
    const { api } = fakeApi({ failBulk });
    renderSection("owner", api);
    const second = await screen.findByRole("textbox", { name: "YAG-1 minimum stok" });
    await waitFor(() => expect((screen.getByRole("textbox", { name: "FRN-1 minimum stok" }) as HTMLInputElement).value).toBe("5"));
    await userEvent.type(second, "3");
    await userEvent.click(screen.getByRole("button", { name: "Minimum stoku kaydet" }));
    expect(await screen.findByText("Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin.")).toBeTruthy();
  });

  it("read-only roles see the values but cannot edit or save", async () => {
    const { api } = fakeApi();
    renderSection("viewer", api);
    const first = await screen.findByRole("textbox", { name: "FRN-1 minimum stok" });
    await waitFor(() => expect((first as HTMLInputElement).value).toBe("5"));
    expect((first as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Minimum stoku kaydet" })).toBeNull();
  });
});

describe("MinimumCell — stock list minimum and 'Kritik' badge", () => {
  it("shows the minimum, the badge only when below, and a dash without a minimum", () => {
    const { rerender } = render(<MinimumCell minQuantity={5} belowMinimum />);
    expect(screen.getByText("Kritik")).toBeTruthy();
    expect(screen.getByText("5")).toBeTruthy();
    rerender(<MinimumCell minQuantity={5} belowMinimum={false} />);
    expect(screen.queryByText("Kritik")).toBeNull();
    rerender(<MinimumCell minQuantity={null} belowMinimum={false} />);
    expect(screen.getByText("—")).toBeTruthy();
  });
});
