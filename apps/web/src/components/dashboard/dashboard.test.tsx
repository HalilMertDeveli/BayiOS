import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import { createAccess } from "@/lib/session/access";
import { grantsFor, type FixturePlan, type FixtureRole } from "@/test/me-fixtures";
import { TEST_USER, mockSession } from "@/test/session";
import { Dashboard } from "./dashboard";

vi.mock("next/navigation", () => ({ usePathname: () => "/pano", useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));

const order = (n: number, grandTotal: number | null) => ({
  id: `o${n}`,
  number: `SIP-${n}`,
  customerId: "c1",
  customerName: "Mock Müşteri",
  warehouseId: "w1",
  warehouseName: "Ana depo",
  status: "pending_approval",
  fulfillmentStatus: "none",
  source: "manual",
  currency: "TRY",
  grandTotal,
  lineCount: 1,
  createdAt: "2026-11-05T09:00:00Z",
  version: 1,
});

type Responses = Record<string, unknown | ApiError>;

/** MOCK API: answers by path prefix; records the paths called. */
function fakeApi(responses: Responses) {
  const paths: string[] = [];
  const api: ApiClient = {
    async request<T>(path: string) {
      paths.push(path);
      const key = Object.keys(responses).find((k) => path.startsWith(k));
      if (!key) throw new ApiError("not mocked", 404, { code: "resource.not_found" });
      const value = responses[key];
      if (value instanceof ApiError) throw value;
      return { data: value as T, status: 200, etag: null };
    },
  };
  return { api, paths };
}

const DEFAULTS: Responses = {
  "/api/v1/orders?": { items: [order(1, 120), order(2, null)], page: { nextCursor: null, hasMore: false, limit: 50 } },
  "/api/v1/reports/orders": {
    from: "2026-11-05",
    to: "2026-11-05",
    amountsVisible: true,
    byStatus: [
      { status: "pending_approval", currency: "TRY", orderCount: 2, grandTotal: 240 },
      { status: "cancelled", currency: "TRY", orderCount: 1, grandTotal: 50 },
    ],
    days: [],
  },
  "/api/v1/reports/critical-stock": {
    mode: "thresholds",
    threshold: null,
    warehouseId: null,
    includeOutOfStock: false,
    thresholdsConfigured: true,
    items: [{ id: "v1", productId: "p1", sku: "KRT-1", name: "Kritik", unit: "ADET", onHand: 2, reserved: 0, available: 2, minQuantity: 5 }],
    page: { nextCursor: "x", hasMore: true, limit: 100 },
  },
  "/api/v1/picking-tasks/mine": {
    items: [
      { id: "t1", type: "pick", status: "open", orderId: "o1", orderNumber: "SIP-1", warehouseId: "w1", assignedTo: TEST_USER.id, assigneeName: null, lineCount: 1, quantityToPick: 3, quantityPicked: 1, quantityShort: 0, createdAt: "2026-11-05T09:00:00Z", startedAt: null, completedAt: null, version: 1 },
      { id: "t2", type: "pick", status: "open", orderId: "o2", orderNumber: "SIP-2", warehouseId: "w1", assignedTo: null, assigneeName: null, lineCount: 1, quantityToPick: 1, quantityPicked: 0, quantityShort: 0, createdAt: "2026-11-05T09:00:00Z", startedAt: null, completedAt: null, version: 1 },
    ],
  },
  "/api/v1/ai/order-parses": { items: [] },
};

function renderAs(role: FixtureRole, responses: Responses = DEFAULTS, plan: FixturePlan = "trial") {
  const { api, paths } = fakeApi(responses);
  render(
    <SessionContext value={mockSession(role, { api, plan })}>
      <Dashboard />
    </SessionContext>,
  );
  return { paths };
}

const tile = (name: string) => screen.getByRole("region", { name });

describe("Dashboard per role (MOCK /me permissions, MOCK API)", () => {
  it("owner: every tile; counts, money, a full critical-stock page shows N+", async () => {
    const { paths } = renderAs("owner");
    expect(await within(tile("Onay bekleyen siparişler")).findByText("2")).toBeTruthy();
    expect(within(tile("Onay bekleyen siparişler")).getByText("₺120,00")).toBeTruthy();
    expect(await within(tile("Bugünkü siparişler")).findByText("3")).toBeTruthy();
    // Amounts without cancelled orders.
    expect(within(tile("Bugünkü siparişler")).getByText("₺240,00")).toBeTruthy();
    expect(await within(tile("Kritik stok")).findByText("1+")).toBeTruthy();
    expect(await within(tile("Görevlerim")).findByText("Havuzda 1 görev bekliyor.")).toBeTruthy();
    expect(await within(tile("İncelenecek WhatsApp siparişleri")).findByText("İnceleme bekleyen ayrıştırma yok.")).toBeTruthy();
    expect(within(tile("Paket")).getByText("Profesyonel")).toBeTruthy();
    expect(within(tile("Paket")).getByText(/Ödeme alınmaz/)).toBeTruthy();
    // One request per tile, nothing else.
    expect(paths.map((p) => p.split("?")[0]).sort()).toEqual(
      ["/api/v1/ai/order-parses", "/api/v1/orders", "/api/v1/picking-tasks/mine", "/api/v1/reports/critical-stock", "/api/v1/reports/orders"].sort(),
    );
    expect(paths.find((p) => p.startsWith("/api/v1/orders?"))).toContain("status=pending_approval");
  });

  it("sales: no critical stock / tasks / plan tiles and no request for them", async () => {
    const { paths } = renderAs("sales");
    expect(await within(tile("Onay bekleyen siparişler")).findByText("2")).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Kritik stok" })).toBeNull();
    expect(screen.queryByRole("region", { name: "Görevlerim" })).toBeNull();
    expect(screen.queryByRole("region", { name: "Paket" })).toBeNull();
    expect(paths.some((p) => p.includes("critical-stock") || p.includes("picking-tasks"))).toBe(false);
  });

  it("viewer: no money when the API hides amounts", async () => {
    renderAs("viewer", {
      ...DEFAULTS,
      "/api/v1/orders?": { items: [order(1, null)], page: { nextCursor: null, hasMore: false, limit: 50 } },
      "/api/v1/reports/orders": { from: "x", to: "x", amountsVisible: false, byStatus: [{ status: "approved", currency: "TRY", orderCount: 1, grandTotal: null }], days: [] },
    });
    expect(await within(tile("Bugünkü siparişler")).findByText("sipariş bugün oluşturuldu")).toBeTruthy();
    expect(screen.queryByText(/₺/)).toBeNull();
    expect(screen.queryByText(/Tutar/)).toBeNull();
    expect(screen.queryByRole("navigation", { name: "Hızlı işlemler" })).toBeNull();
  });

  it("no money without prices.read.sell, even if an amount were sent", async () => {
    const { api } = fakeApi(DEFAULTS);
    const withoutPrices = createAccess(grantsFor("viewer").filter((g) => g.permission !== "prices.read.sell"));
    render(
      <SessionContext value={mockSession("viewer", { api, access: withoutPrices })}>
        <Dashboard />
      </SessionContext>,
    );
    expect(await within(tile("Onay bekleyen siparişler")).findByText("2")).toBeTruthy();
    expect(await within(tile("Bugünkü siparişler")).findByText("sipariş bugün oluşturuldu")).toBeTruthy();
    expect(screen.queryByText(/₺/)).toBeNull();
  });

  it("a failing tile shows its own error with a retry; the other tiles still load", async () => {
    let fail = true;
    const responses: Responses = { ...DEFAULTS };
    const { api } = fakeApi(responses);
    const flaky: ApiClient = {
      request: async <T,>(path: string, options?: Parameters<ApiClient["request"]>[1]) => {
        if (path.startsWith("/api/v1/reports/critical-stock") && fail) throw new ApiError("boom", 500, { code: "server.error" });
        return api.request<T>(path, options);
      },
    };
    render(
      <SessionContext value={mockSession("manager", { api: flaky })}>
        <Dashboard />
      </SessionContext>,
    );
    const critical = tile("Kritik stok");
    expect(await within(critical).findByRole("alert")).toBeTruthy();
    expect(await within(tile("Onay bekleyen siparişler")).findByText("2")).toBeTruthy();
    fail = false;
    await userEvent.click(within(critical).getByRole("button", { name: "Tekrar dene" }));
    expect(await within(critical).findByText("1+")).toBeTruthy();
  });

  it("critical stock tile: counts variants under their minimum, asks for thresholds mode (#137)", async () => {
    const { paths } = renderAs("owner");
    const critical = tile("Kritik stok");
    expect(await within(critical).findByText("ürün minimum stokta veya altında")).toBeTruthy();
    expect(within(critical).getByText(/satılabilir 2 \/ min\. 5 ADET/)).toBeTruthy();
    const call = paths.find((p) => p.startsWith("/api/v1/reports/critical-stock"))!;
    const query = new URLSearchParams(call.split("?")[1]);
    expect(query.has("threshold")).toBe(false);
    expect(query.has("includeOutOfStock")).toBe(false);
  });

  it("critical stock tile: empty state explains how to set thresholds, or that nothing is under its minimum", async () => {
    const none = { mode: "thresholds", threshold: null, warehouseId: null, includeOutOfStock: false, items: [], page: { nextCursor: null, hasMore: false, limit: 100 } };
    renderAs("manager", { ...DEFAULTS, "/api/v1/reports/critical-stock": { ...none, thresholdsConfigured: false } });
    expect(await within(tile("Kritik stok")).findByText(/Henüz minimum stok tanımlanmadı/)).toBeTruthy();
  });

  it("critical stock tile: thresholds set and nothing critical", async () => {
    const none = { mode: "thresholds", threshold: null, warehouseId: null, includeOutOfStock: false, items: [], page: { nextCursor: null, hasMore: false, limit: 100 } };
    renderAs("manager", { ...DEFAULTS, "/api/v1/reports/critical-stock": { ...none, thresholdsConfigured: true } });
    expect(await within(tile("Kritik stok")).findByText("Minimum stoğun altına inen ürün yok.")).toBeTruthy();
  });

  it("empty states: nothing pending, nothing today", async () => {
    renderAs("manager", {
      ...DEFAULTS,
      "/api/v1/orders?": { items: [], page: { nextCursor: null, hasMore: false, limit: 50 } },
      "/api/v1/reports/orders": { from: "x", to: "x", amountsVisible: true, byStatus: [], days: [] },
    });
    expect(await screen.findByText("Onay bekleyen sipariş yok.")).toBeTruthy();
    expect(await screen.findByText("Bugün henüz sipariş oluşturulmadı.")).toBeTruthy();
  });
});
