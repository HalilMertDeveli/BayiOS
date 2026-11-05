import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { ToastProvider } from "@/components/ui/toast";
import { ApiError, type ApiClient } from "@/lib/api/client";
import type { OrderDetail } from "@/lib/orders/api";
import { orderDetail, orderLine } from "@/lib/orders/test-fixtures";
import { SessionContext } from "@/lib/session/session-context";
import type { FixtureRole, FixtureRules } from "@/test/me-fixtures";
import { TEST_USER, mockSession } from "@/test/session";
import { OrderDetailPage } from "./order-detail";

vi.mock("next/navigation", () => ({ usePathname: () => "/siparisler/ord-1", useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));

const ME = TEST_USER.id;

/** MOCK API: the order detail only; every other lookup (warehouses, customers) answers 404 and the page degrades. */
function fakeApi(detail: OrderDetail) {
  const paths: string[] = [];
  const api: ApiClient = {
    async request<T>(path: string) {
      paths.push(path);
      if (path === "/api/v1/orders/ord-1") return { data: detail as T, status: 200, etag: 'W/"3"' };
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  return { api, paths };
}

function renderAs(role: FixtureRole, detail: OrderDetail, rules: Partial<FixtureRules> = {}) {
  const { api, paths } = fakeApi(detail);
  render(
    <SessionContext value={mockSession(role, { api, rules })}>
      <ToastProvider>
        <OrderDetailPage orderId="ord-1" />
      </ToastProvider>
    </SessionContext>,
  );
  return { paths };
}

const draft = (createdBy: string | null) => orderDetail([orderLine()], { status: "draft", createdBy });
const actionNames = () => {
  const section = screen.queryByRole("region", { name: "İşlemler" });
  return section ? within(section).getAllByRole("button").map((b) => b.textContent) : [];
};

describe("OrderDetailPage per role (MOCK /me permissions)", () => {
  it("owner edits any draft and gets submit + cancel", async () => {
    renderAs("owner", draft("someone-else"));
    expect(await screen.findByRole("form", { name: "Taslak sipariş" })).toBeTruthy();
    expect(actionNames()).toEqual(["Onaya gönder", "Siparişi iptal et"]);
    expect(screen.getByLabelText("LED-1 manuel birim fiyat")).toBeTruthy();
  });

  it("sales edits and cancels its own draft; no manual price or discount while the tenant limit is 0", async () => {
    renderAs("sales", draft(ME));
    expect(await screen.findByRole("form", { name: "Taslak sipariş" })).toBeTruthy();
    expect(actionNames()).toEqual(["Onaya gönder", "Siparişi iptal et"]);
    expect(screen.queryByLabelText("LED-1 manuel birim fiyat")).toBeNull();
    expect(screen.queryByLabelText("LED-1 iskonto türü")).toBeNull();
  });

  it("sales under a discount limit gets the discount field but still no manual price (K¹⁰)", async () => {
    renderAs("sales", draft(ME), { salesMaxDiscountPercent: 10 });
    expect(await screen.findByLabelText("LED-1 iskonto türü")).toBeTruthy();
    expect(screen.getByText("İşletmenizin iskonto sınırı içinde.")).toBeTruthy();
    expect(screen.queryByLabelText("LED-1 manuel birim fiyat")).toBeNull();
  });

  it("sales sees another user's draft read-only and cannot cancel it", async () => {
    renderAs("sales", draft("someone-else"));
    expect(await screen.findByText(/Bu taslağı başka bir kullanıcı oluşturdu/)).toBeTruthy();
    expect(screen.queryByRole("form", { name: "Taslak sipariş" })).toBeNull();
    expect(actionNames()).toEqual(["Onaya gönder"]);
  });

  it("warehouse_staff opens a task-linked order: prices '—' (never ₺0 or 'Fiyat yok'), no actions, no customer lookup", async () => {
    const hidden = orderDetail(
      [orderLine({ unitPrice: null, priceSource: null, priceListId: null, lineSubtotal: null, lineDiscount: null, lineTotal: null, reservedQuantity: 2 })],
      { status: "approved", totals: null, createdBy: "someone-else" },
    );
    const { paths } = renderAs("warehouse_staff", hidden);
    expect(await screen.findByText(/Fiyat ve tutarlar rolünüz gereği gösterilmiyor/)).toBeTruthy();
    expect(screen.queryByText("Fiyat yok")).toBeNull();
    expect(screen.queryByText(/₺/)).toBeNull();
    expect(screen.getAllByText("Fiyat gösterilmiyor").length).toBeGreaterThan(0);
    expect(screen.queryByRole("heading", { name: /Toplamlar/ })).toBeNull();
    expect(actionNames()).toEqual([]);
    expect(paths.some((p) => p.startsWith("/api/v1/customers"))).toBe(false);
  });

  it("viewer reads without editor or actions", async () => {
    renderAs("viewer", draft("someone-else"));
    expect(await screen.findByRole("heading", { name: "Satırlar" })).toBeTruthy();
    expect(screen.queryByRole("form", { name: "Taslak sipariş" })).toBeNull();
    expect(actionNames()).toEqual([]);
  });
});
