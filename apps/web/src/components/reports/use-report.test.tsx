import { describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useReportUrl } from "./use-report";

// MOCK router: replace() records the URL but the search params do NOT change (the navigation has not committed yet).
const replace = vi.fn();
let params = new URLSearchParams();
vi.mock("next/navigation", () => ({
  usePathname: () => "/raporlar/siparisler",
  useRouter: () => ({ replace, push: vi.fn() }),
  useSearchParams: () => params,
}));

describe("useReportUrl while a navigation is pending (#152)", () => {
  it("a filter and then a date range keep both", () => {
    replace.mockClear();
    params = new URLSearchParams();
    const { result } = renderHook(() => useReportUrl());
    act(() => result.current.update({ durum: "open" }));
    act(() => result.current.setRange({ from: "2026-10-01", to: "2026-10-31" }));
    expect(replace).toHaveBeenLastCalledWith("/raporlar/siparisler?durum=open&baslangic=2026-10-01&bitis=2026-10-31", { scroll: false });
  });

  it("removing a filter and then another change: the removed filter does not come back", () => {
    replace.mockClear();
    params = new URLSearchParams("depo=w-1&esik=5");
    const { result } = renderHook(() => useReportUrl());
    act(() => result.current.update({ depo: null }));
    expect(replace).toHaveBeenLastCalledWith("/raporlar/siparisler?esik=5", { scroll: false });
    act(() => result.current.update({ esik: "7" }));
    expect(replace).toHaveBeenLastCalledWith("/raporlar/siparisler?esik=7", { scroll: false });
  });

  it("follows the URL again once it has caught up", () => {
    replace.mockClear();
    params = new URLSearchParams();
    const { result, rerender } = renderHook(() => useReportUrl());
    act(() => result.current.update({ durum: "open" }));
    params = new URLSearchParams("durum=open");
    rerender();
    // A history navigation (back) to another URL: changes build on it, not on the earlier request.
    params = new URLSearchParams("depo=w-2");
    rerender();
    act(() => result.current.update({ esik: "3" }));
    expect(replace).toHaveBeenLastCalledWith("/raporlar/siparisler?depo=w-2&esik=3", { scroll: false });
  });
});
