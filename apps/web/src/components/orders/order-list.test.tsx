import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { OrderList } from "./order-list";

// MOCK router: replace() records the URL but the search params do NOT change, i.e. the navigation transition has not
// committed yet. That is the window in which a second filter change used to start from the stale URL (#136).
const replace = vi.fn();
let params = new URLSearchParams();
vi.mock("next/navigation", () => ({
  usePathname: () => "/siparisler",
  useRouter: () => ({ replace, push: vi.fn() }),
  useSearchParams: () => params,
}));

const api: ApiClient = {
  async request<T>() {
    return { data: { items: [], page: { nextCursor: null, hasMore: false, limit: 50 } } as T, status: 200, etag: null };
  },
};

function renderList() {
  render(
    <SessionContext value={mockSession("owner", { api })}>
      <OrderList />
    </SessionContext>,
  );
}

describe("OrderList filters while a navigation is pending (#136)", () => {
  it("'clear' and then a date: the date builds on the cleared URL, the old source filter does not come back", async () => {
    replace.mockClear();
    params = new URLSearchParams("kaynak=whatsapp_ai");
    renderList();
    await userEvent.click((await screen.findAllByRole("button", { name: "Aramayı ve filtreleri temizle" }))[0]);
    expect(replace).toHaveBeenLastCalledWith("/siparisler", { scroll: false });

    fireEvent.change(screen.getByLabelText("Başlangıç tarihi"), { target: { value: "2026-11-02" } });
    expect(replace).toHaveBeenLastCalledWith("/siparisler?baslangic=2026-11-02", { scroll: false });
    fireEvent.change(screen.getByLabelText("Bitiş tarihi"), { target: { value: "2026-11-03" } });
    expect(replace).toHaveBeenLastCalledWith("/siparisler?baslangic=2026-11-02&bitis=2026-11-03", { scroll: false });
  });

  it("two quick filter changes keep both", async () => {
    replace.mockClear();
    params = new URLSearchParams();
    renderList();
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Kaynak" }), "whatsapp_ai");
    fireEvent.change(screen.getByLabelText("Başlangıç tarihi"), { target: { value: "2026-11-02" } });
    expect(replace).toHaveBeenLastCalledWith("/siparisler?kaynak=whatsapp_ai&baslangic=2026-11-02", { scroll: false });
  });
});
