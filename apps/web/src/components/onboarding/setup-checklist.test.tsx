import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import type { FixtureRole } from "@/test/me-fixtures";
import { mockSession } from "@/test/session";
import { SetupChecklist } from "./setup-checklist";

vi.mock("next/navigation", () => ({ usePathname: () => "/pano", useRouter: () => ({ replace: vi.fn(), push: vi.fn() }) }));

const page = (items: unknown[]) => ({ items, page: { nextCursor: null, hasMore: false, limit: 1 } });

/** MOCK API by path prefix (longest match first); records the paths called. */
function fakeApi(responses: Record<string, unknown>) {
  const paths: string[] = [];
  const keys = Object.keys(responses).sort((a, b) => b.length - a.length);
  const api: ApiClient = {
    async request<T>(path: string) {
      paths.push(path);
      const key = keys.find((k) => path.startsWith(k));
      if (!key) throw new ApiError("not mocked", 404, { code: "resource.not_found" });
      return { data: responses[key] as T, status: 200, etag: null };
    },
  };
  return { api, paths };
}

/** A brand-new business: one warehouse, nothing else yet; the owner is the only member. */
const NEW_TENANT = {
  "/api/v1/warehouses": { items: [{ id: "w1" }] },
  "/api/v1/products": page([]),
  "/api/v1/inventory/balances": page([]),
  "/api/v1/members/invitations": { items: [] },
  "/api/v1/members": page([{ userId: "u-1" }]),
  "/api/v1/orders": page([]),
};

function renderAs(role: FixtureRole, responses: Record<string, unknown> = NEW_TENANT) {
  const { api, paths } = fakeApi(responses);
  render(
    <SessionContext value={mockSession(role, { api })}>
      <SetupChecklist />
    </SessionContext>,
  );
  return { paths };
}

afterEach(() => window.localStorage.clear());

describe("İlk adımlar checklist (MOCK API)", () => {
  it("shows progress computed from data, with an action for each open step", async () => {
    renderAs("owner");
    const section = await screen.findByRole("region", { name: "İlk adımlar" });
    expect(within(section).getByText("5 adımdan 1 tanesi tamamlandı.")).toBeTruthy();
    expect(within(section).getByRole("progressbar", { name: "Kurulum ilerlemesi" }).getAttribute("aria-valuenow")).toBe("1");
    expect(within(section).getByRole("heading", { name: /Deponuzu tanımlayın \(tamamlandı\)/ })).toBeTruthy();
    expect(within(section).queryByRole("link", { name: "Depoyu kur" })).toBeNull();
    expect(within(section).getByRole("link", { name: "Ürünleri ekle" }).getAttribute("href")).toBe("/kurulum?adim=urunler");
    expect(within(section).getByRole("link", { name: "Açılış stoğunu gir" }).getAttribute("href")).toBe("/kurulum?adim=stok");
    expect(within(section).getByRole("link", { name: "Ekibi davet et" }).getAttribute("href")).toBe("/kurulum?adim=ekip");
    expect(within(section).getByRole("link", { name: "İlk siparişi aç" }).getAttribute("href")).toBe("/siparisler/yeni");
  });

  it("counts an invitation as team progress and leaves out facts that could not be read", async () => {
    renderAs("owner", {
      ...NEW_TENANT,
      "/api/v1/products": page([{ id: "p1" }]),
      "/api/v1/members/invitations": { items: [{ id: "i1" }] },
      "/api/v1/inventory/balances": undefined, // a response without items: the read fails
    });
    const section = await screen.findByRole("region", { name: "İlk adımlar" });
    // balances answered `undefined` → the read throws → unknown → not listed (4 rows), never shown as done.
    expect(within(section).getByText("4 adımdan 3 tanesi tamamlandı.")).toBeTruthy();
    expect(within(section).queryByText(/Açılış stoğunu girin/)).toBeNull();
  });

  it("can be dismissed, per user and tenant", async () => {
    renderAs("owner");
    const section = await screen.findByRole("region", { name: "İlk adımlar" });
    await userEvent.click(within(section).getByRole("button", { name: "İlk adımlar listesini kapat" }));
    expect(screen.queryByRole("region", { name: "İlk adımlar" })).toBeNull();
    expect(window.localStorage.getItem("bayios.setup-checklist.dismissed.u-1.t-1")).toBe("1");
  });

  it("is not shown (and reads nothing) for roles that do not set the business up", async () => {
    const { paths } = renderAs("sales");
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByRole("region", { name: "İlk adımlar" })).toBeNull();
    expect(paths).toEqual([]);
  });
});
