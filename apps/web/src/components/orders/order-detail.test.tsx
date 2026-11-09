import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { ToastProvider } from "@/components/ui/toast";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import type { OrderDetail } from "@/lib/orders/api";
import { orderDetail, orderLine } from "@/lib/orders/test-fixtures";
import { SessionContext } from "@/lib/session/session-context";
import type { FixtureRole, FixtureRules } from "@/test/me-fixtures";
import { frameBeforeCommit } from "@/test/frame-before-commit";
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
    // The cap comes from /me (permissions[].parameters.maxDiscountPercent, #111).
    expect(screen.getByText("İskonto en fazla %10")).toBeTruthy();
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

  it("shows the names the API joins in: customer, warehouse, creator, approver and history (no per-customer lookup)", async () => {
    const detail = orderDetail([orderLine()], {
      status: "approved",
      customerName: "Örnek Bayi",
      warehouseName: "Merkez Depo",
      createdBy: "someone-else",
      createdByName: "Ayşe Satış",
      approvedAt: "2026-11-05T10:00:00Z",
      approvedBy: ME,
      approvedByName: "Mert Yönetici",
    });
    detail.history = [
      { id: "h1", action: "create", fromStatus: null, toStatus: "draft", fulfillmentStatus: "none", reason: null, changedBy: "someone-else", changedByName: "Ayşe Satış", changedAt: "2026-11-05T09:00:00Z" },
      { id: "h2", action: "approve", fromStatus: "pending_approval", toStatus: "approved", fulfillmentStatus: "none", reason: null, changedBy: ME, changedByName: "Mert Yönetici", changedAt: "2026-11-05T10:00:00Z" },
    ];
    const { paths } = renderAs("owner", detail);
    expect(await screen.findAllByText("Örnek Bayi")).not.toHaveLength(0);
    expect(screen.getByText("Merkez Depo")).toBeTruthy();
    expect(screen.getByText(/Mert Yönetici \(siz\)/, { selector: "dd" })).toBeTruthy();
    const history = within(screen.getByRole("list", { name: "Durum geçmişi" }));
    expect(history.getAllByText(/Ayşe Satış/).length).toBe(1);
    expect(history.getByText(/Mert Yönetici \(siz\)/)).toBeTruthy();
    expect(paths.some((p) => p.startsWith("/api/v1/customers"))).toBe(false);
  });

  it("viewer reads without editor or actions", async () => {
    renderAs("viewer", draft("someone-else"));
    expect(await screen.findByRole("heading", { name: "Satırlar" })).toBeTruthy();
    expect(screen.queryByRole("form", { name: "Taslak sipariş" })).toBeNull();
    expect(actionNames()).toEqual([]);
  });
});

/** MOCK API for transitions: GET the order; POST submit answers with `onSubmit(body, call)` (or throws). */
function transitionApi(detail: OrderDetail, onSubmit: (body: unknown, call: number) => OrderDetail) {
  const posts: { path: string; body: unknown; key: string | undefined }[] = [];
  const api: ApiClient = {
    async request<T>(path: string, options: RequestOptions = {}) {
      if (options.method === "POST") {
        posts.push({ path, body: options.body, key: options.idempotencyKey });
        return { data: onSubmit(options.body, posts.length) as T, status: 200, etag: 'W/"4"' };
      }
      if (path === "/api/v1/orders/ord-1") return { data: detail as T, status: 200, etag: 'W/"3"' };
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  return { api, posts };
}

function renderWith(api: ApiClient, role: FixtureRole = "owner") {
  render(
    <SessionContext value={mockSession(role, { api })}>
      <ToastProvider>
        <OrderDetailPage orderId="ord-1" />
      </ToastProvider>
    </SessionContext>,
  );
}

const SHORTAGE = { variantId: "var-1", sku: "LED-1", warehouseId: "wh-1", locationId: null, requested: 2, available: 1 };

describe("submit when the tenant needs no approval (#92)", () => {
  it("an approved answer is shown as 'otomatik onaylandı' with the approved state", async () => {
    const approved = orderDetail([orderLine({ reservedQuantity: 2 })], { status: "approved", approvedAt: "2026-11-05T10:00:00Z", version: 4 });
    const { api, posts } = transitionApi(draft(ME), () => approved);
    renderWith(api);
    await userEvent.click(await screen.findByRole("button", { name: "Onaya gönder" }));
    expect(await screen.findByText("SIP-2026-000001: Sipariş otomatik onaylandı; stok ayrıldı")).toBeTruthy();
    expect(screen.getByText(/sipariş onayı zorunlu değil/)).toBeTruthy();
    expect(screen.getAllByText("Onaylandı").length).toBeGreaterThan(0);
    expect(posts.map((p) => p.path)).toEqual(["/api/v1/orders/ord-1/submit"]);
  });

  it("a shortage never offers a negative-stock confirmation (OD-09), even if a problem still carries confirmationRequired", async () => {
    const { api, posts } = transitionApi(draft(ME), () => {
      throw new ApiError("x", 409, { code: "inventory.insufficient_stock", confirmationRequired: true, shortages: [SHORTAGE] });
    });
    renderWith(api);
    await userEvent.click(await screen.findByRole("button", { name: "Onaya gönder" }));
    expect(await screen.findByRole("heading", { name: "Gönderilemedi: stok yetersiz" })).toBeTruthy();
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(screen.queryByRole("button", { name: /Yine de/ })).toBeNull();
    expect(posts.map((p) => p.body)).toEqual([undefined]);
  });

  it("the shortages panel says the order stayed a draft", async () => {
    const { api } = transitionApi(draft(ME), () => {
      throw new ApiError("x", 409, { code: "inventory.insufficient_stock", shortages: [SHORTAGE] });
    });
    renderWith(api);
    await userEvent.click(await screen.findByRole("button", { name: "Onaya gönder" }));
    expect(await screen.findByRole("heading", { name: "Gönderilemedi: stok yetersiz" })).toBeTruthy();
    expect(screen.getByText(/taslak olarak kaldı/)).toBeTruthy();
    expect(screen.getByRole("list", { name: "Stoğu yetmeyen satırlar" }).textContent).toContain("istenen 2, satılabilir 1");
  });

  it("the shortages heading takes focus even when a frame runs before the update commits (#152)", async () => {
    frameBeforeCommit();
    try {
      const { api } = transitionApi(draft(ME), () => {
        throw new ApiError("x", 409, { code: "inventory.insufficient_stock", shortages: [SHORTAGE] });
      });
      renderWith(api);
      await userEvent.click(await screen.findByRole("button", { name: "Onaya gönder" }));
      const heading = await screen.findByRole("heading", { name: "Gönderilemedi: stok yetersiz" });
      await waitFor(() => expect(document.activeElement).toBe(heading));
    } finally {
      vi.restoreAllMocks();
    }
  });
});

describe("order addresses (#92)", () => {
  const address = { addressId: "s1", title: "Merkez", line1: "Örnek Cd. 1", line2: null, district: "Kadıköy", city: "İstanbul", postalCode: null, country: "TR", contactName: null, contactPhone: null };
  const noPost = () => {
    throw new Error("no post");
  };

  it("shows the snapshot copied at submit", async () => {
    const { api } = transitionApi(
      orderDetail([orderLine()], { status: "pending_approval", shippingAddress: address, billingAddress: null, addressesAreSnapshot: true }),
      noPost,
    );
    renderWith(api);
    const section = await screen.findByRole("region", { name: "Adresler" });
    expect(section.textContent).toContain("Sipariş gönderildiğinde kaydedilen adresler");
    expect(section.textContent).toContain("Örnek Cd. 1");
    expect(section.textContent).toContain("Siparişte kayıtlı adres yok.");
  });

  it("a snapshot masked after the customer was anonymized (#115) is shown with a note", async () => {
    const masked = { ...address, title: null, line1: "Silinmiş adres", line2: null, postalCode: null, contactName: null, contactPhone: null };
    const { api } = transitionApi(orderDetail([orderLine()], { status: "approved", shippingAddress: masked, billingAddress: masked, addressesAreSnapshot: true }), noPost);
    renderWith(api);
    const section = await screen.findByRole("region", { name: "Adresler" });
    expect(section.textContent).toContain("Silinmiş adres");
    expect(section.textContent).toContain("Kadıköy İstanbul, TR");
    expect(within(section).getAllByText(/adres maskelendi/)).toHaveLength(2);
  });

  it("warehouse staff (no customers.read) gets no address section", async () => {
    const { api } = transitionApi(orderDetail([orderLine()], { status: "approved", totals: null }), noPost);
    renderWith(api, "warehouse_staff");
    expect(await screen.findByRole("heading", { name: "Satırlar" })).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Adresler" })).toBeNull();
  });
});
