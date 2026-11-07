import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { StockBalances } from "./stock-balances";

// MOCK router: replace() records the URL but the search params do NOT change (the navigation has not committed yet).
const replace = vi.fn();
let params = new URLSearchParams();
vi.mock("next/navigation", () => ({
  usePathname: () => "/stok",
  useRouter: () => ({ replace, push: vi.fn() }),
  useSearchParams: () => params,
}));

// MOCK warehouses (fixtures, not real data).
const warehouses = [
  { id: "w-1", code: "ANA", name: "Ana depo", status: "active" },
  { id: "w-2", code: "YDK", name: "Yedek depo", status: "active" },
];
const api: ApiClient = {
  async request<T>(path: string) {
    const data = path.startsWith("/api/v1/warehouses") ? { items: warehouses } : { items: [], page: { nextCursor: null, hasMore: false, limit: 50 } };
    return { data: data as T, status: 200, etag: null };
  },
};

function renderList() {
  render(
    <SessionContext value={mockSession("owner", { api })}>
      <StockBalances />
    </SessionContext>,
  );
}

describe("StockBalances filters while a navigation is pending (#152)", () => {
  it("'clear' and then a search: the cleared warehouse does not come back", async () => {
    replace.mockClear();
    params = new URLSearchParams("warehouseId=w-1&q=fren");
    renderList();
    await userEvent.click(await screen.findByRole("button", { name: "Filtreleri temizle" }));
    expect(replace).toHaveBeenLastCalledWith("/stok", { scroll: false });
    const search = screen.getByLabelText("SKU, barkod veya ürün adı");
    await userEvent.clear(search);
    await userEvent.type(search, "SKU-1{Enter}");
    expect(replace).toHaveBeenLastCalledWith("/stok?q=SKU-1", { scroll: false });
  });

  it("a search and then a warehouse keep both", async () => {
    replace.mockClear();
    params = new URLSearchParams();
    renderList();
    await userEvent.type(screen.getByLabelText("SKU, barkod veya ürün adı"), "SKU-1{Enter}");
    const select = screen.getByRole("combobox", { name: "Depo" });
    await vi.waitFor(() => expect((select as HTMLSelectElement).disabled).toBe(false));
    await userEvent.selectOptions(select, "w-2");
    expect(replace).toHaveBeenLastCalledWith("/stok?warehouseId=w-2&q=SKU-1", { scroll: false });
  });
});
