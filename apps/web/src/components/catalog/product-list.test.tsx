import { describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { ProductList } from "./product-list";

// MOCK router: replace() records the URL but the search params do NOT change, i.e. the navigation transition has not
// committed yet. A second change in that window used to start from the stale URL (#136, #152).
const replace = vi.fn();
let params = new URLSearchParams();
vi.mock("next/navigation", () => ({
  usePathname: () => "/urunler",
  useRouter: () => ({ replace, push: vi.fn() }),
  useSearchParams: () => params,
}));

const api: ApiClient = {
  async request<T>(path: string) {
    void path;
    const data = { items: [], page: { nextCursor: null, hasMore: false, limit: 50 } };
    return { data: data as T, status: 200, etag: null };
  },
};

function renderList() {
  render(
    <SessionContext value={mockSession("owner", { api })}>
      <ProductList />
    </SessionContext>,
  );
}

describe("ProductList filters while a navigation is pending (#152)", () => {
  it("two quick filter changes keep both", async () => {
    replace.mockClear();
    params = new URLSearchParams();
    renderList();
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Durum" }), "archived");
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Sıralama" }), "name");
    expect(replace).toHaveBeenLastCalledWith("/urunler?status=archived&sort=name", { scroll: false });
  });

  it("'clear' and then a filter: the cleared filters do not come back", async () => {
    replace.mockClear();
    params = new URLSearchParams("brandId=b-1&status=inactive");
    renderList();
    await userEvent.click((await screen.findAllByRole("button", { name: /temizle/i }))[0]);
    expect(replace).toHaveBeenLastCalledWith("/urunler", { scroll: false });
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Sıralama" }), "name");
    expect(replace).toHaveBeenLastCalledWith("/urunler?sort=name", { scroll: false });
  });

  it("a filter and then the debounced search keep both", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      replace.mockClear();
      params = new URLSearchParams();
      renderList();
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.selectOptions(await screen.findByRole("combobox", { name: "Durum" }), "archived");
      await user.type(await screen.findByLabelText("Ürün ara"), "fren");
      await act(async () => {
        vi.advanceTimersByTime(400);
      });
      expect(replace).toHaveBeenLastCalledWith("/urunler?q=fren&status=archived", { scroll: false });
    } finally {
      vi.useRealTimers();
    }
  });
});
