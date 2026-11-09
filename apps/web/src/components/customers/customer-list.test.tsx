import { describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { CustomerList } from "./customer-list";

// MOCK router: replace() records the URL but the search params do NOT change (the navigation has not committed yet).
const replace = vi.fn();
let params = new URLSearchParams();
vi.mock("next/navigation", () => ({
  usePathname: () => "/musteriler",
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
      <CustomerList />
    </SessionContext>,
  );
}

describe("CustomerList filters while a navigation is pending (#152)", () => {
  it("'clear' and then a status: the cleared group filter does not come back", async () => {
    replace.mockClear();
    params = new URLSearchParams("grup=g-1&durum=inactive");
    renderList();
    await userEvent.click((await screen.findAllByRole("button", { name: /temizle/i }))[0]);
    expect(replace).toHaveBeenLastCalledWith("/musteriler", { scroll: false });
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Durum" }), "archived");
    expect(replace).toHaveBeenLastCalledWith("/musteriler?durum=archived", { scroll: false });
  });

  it("a status and then the debounced search keep both", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      replace.mockClear();
      params = new URLSearchParams();
      renderList();
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.selectOptions(await screen.findByRole("combobox", { name: "Durum" }), "archived");
      await user.type(screen.getByLabelText("Müşteri ara"), "yıldız");
      await act(async () => {
        vi.advanceTimersByTime(400);
      });
      expect(replace).toHaveBeenLastCalledWith(`/musteriler?q=${encodeURIComponent("yıldız")}&durum=archived`, { scroll: false });
    } finally {
      vi.useRealTimers();
    }
  });
});
