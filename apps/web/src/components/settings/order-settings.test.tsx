import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ToastProvider } from "@/components/ui/toast";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import type { TenantSettings } from "@/lib/api/identity";
import { SessionContext } from "@/lib/session/session-context";
import type { FixtureRole } from "@/test/me-fixtures";
import { frameBeforeCommit } from "@/test/frame-before-commit";
import { mockSession } from "@/test/session";
import { OrderSettings } from "./order-settings";

/** Synthetic settings (MOCK). */
const SETTINGS: TenantSettings = {
  negativeStockPolicy: "block",
  salesCanApproveOrders: false,
  salesMaxDiscountPercent: 0,
  showCostToManager: true,
  showCostToViewer: false,
  staffCanAssignBarcode: false,
  staffCanApproveReceipt: false,
  require2fa: false,
  defaultTaxRate: 20,
  pricesIncludeTaxDisplay: false,
  requireOrderApproval: true,
  orderNumberPrefix: "SIP",
};

type Call = { path: string; options: RequestOptions };

/** MOCK GET/PATCH /tenant/settings with an ETag version; `conflictOnce` answers the first PATCH with 412. */
function fakeApi(options: { conflictOnce?: boolean } = {}) {
  const calls: Call[] = [];
  let version = 4;
  let current = { ...SETTINGS };
  let conflict = options.conflictOnce ?? false;
  const api: ApiClient = {
    async request<T>(path: string, opts: RequestOptions = {}) {
      calls.push({ path, options: opts });
      if (opts.method === "PATCH") {
        if (conflict) {
          conflict = false;
          version += 1; // someone else saved meanwhile
          throw new ApiError("x", 412, { code: "concurrency.version_mismatch" });
        }
        if (opts.ifMatch !== `W/"${version}"`) throw new ApiError("x", 412, { code: "concurrency.version_mismatch" });
        current = { ...current, ...(opts.body as object) };
        version += 1;
      }
      return { data: current as T, status: 200, etag: `W/"${version}"` };
    },
  };
  return { api, calls };
}

function renderAs(role: FixtureRole, api: ApiClient) {
  render(
    <SessionContext value={mockSession(role, { api })}>
      <ToastProvider>
        <OrderSettings />
      </ToastProvider>
    </SessionContext>,
  );
}

const patches = (calls: Call[]) => calls.filter((c) => c.options.method === "PATCH");

describe("Ayarlar → Sipariş ayarları (#92)", () => {
  it("is locked without tenant.settings.manage (no request)", () => {
    const { api, calls } = fakeApi();
    renderAs("manager", api);
    expect(screen.getByText("Ayarları değiştirme yetkiniz yok")).toBeTruthy();
    expect(calls).toEqual([]);
  });

  it("saves only the changes as a merge patch with If-Match; the prefix is validated first", async () => {
    const { api, calls } = fakeApi();
    renderAs("owner", api);
    const prefix = await screen.findByRole("textbox", { name: /Sipariş numarası öneki/ });
    expect((screen.getByRole("checkbox", { name: "Siparişler onaydan geçsin" }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("button", { name: "Ayarları kaydet" }) as HTMLButtonElement).disabled).toBe(true);

    await userEvent.clear(prefix);
    await userEvent.type(prefix, "1web");
    await userEvent.click(screen.getByRole("button", { name: "Ayarları kaydet" }));
    expect(screen.getByText("Önek bir harfle (A–Z) başlamalı.")).toBeTruthy();
    expect(patches(calls)).toHaveLength(0);

    await userEvent.clear(prefix);
    await userEvent.type(prefix, "web");
    expect((prefix as HTMLInputElement).value).toBe("WEB");
    expect(screen.getByText(/WEB-\d{4}-000123/)).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "Siparişler onaydan geçsin" }));
    expect(screen.getByText(/hemen onaylanır ve stok ayrılır/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Ayarları kaydet" }));

    expect(await screen.findByText("Sipariş ayarları kaydedildi")).toBeTruthy();
    const [patch] = patches(calls);
    expect(patch.path).toBe("/api/v1/tenant/settings");
    expect(patch.options.ifMatch).toBe('W/"4"');
    expect(patch.options.contentType).toBe("application/merge-patch+json");
    expect(patch.options.body).toEqual({ requireOrderApproval: false, orderNumberPrefix: "WEB" });
    // Saved: the form is clean again with the new ETag.
    expect((await screen.findByRole("button", { name: "Ayarları kaydet" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("412: someone else saved first → reload notice; reloading shows the current settings", async () => {
    const { api, calls } = fakeApi({ conflictOnce: true });
    renderAs("admin", api);
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Stok yetmediğinde" }), "warn");
    await userEvent.click(screen.getByRole("button", { name: "Ayarları kaydet" }));
    expect(await screen.findByText("Ayarlar başkası tarafından değiştirildi")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Ayarları kaydet" }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole("button", { name: "Güncel ayarları yükle" }));
    expect(await screen.findByRole("combobox", { name: "Stok yetmediğinde" })).toHaveProperty("value", "block");
    expect(screen.queryByText("Ayarlar başkası tarafından değiştirildi")).toBeNull();
    expect(calls.filter((c) => !c.options.method || c.options.method === "GET")).toHaveLength(2);
  });

  it("a failed save focuses the error even when a frame runs before the update commits (#152)", async () => {
    frameBeforeCommit();
    try {
      const { api } = fakeApi();
      const failing: ApiClient = {
        request<T>(path: string, opts: RequestOptions = {}) {
          if (opts.method === "PATCH") return Promise.reject(new ApiError("x", 500, { code: "server.error" }));
          return api.request<T>(path, opts);
        },
      };
      renderAs("admin", failing);
      await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Stok yetmediğinde" }), "warn");
      await userEvent.click(screen.getByRole("button", { name: "Ayarları kaydet" }));
      const alert = (await screen.findByText("Ayarlar kaydedilemedi")).closest('[tabindex="-1"]');
      await waitFor(() => expect(document.activeElement).toBe(alert));
    } finally {
      vi.restoreAllMocks();
    }
  });

  it("412: the reload notice takes focus (the save button it came from is disabled now)", async () => {
    const { api } = fakeApi({ conflictOnce: true });
    renderAs("admin", api);
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Stok yetmediğinde" }), "warn");
    await userEvent.click(screen.getByRole("button", { name: "Ayarları kaydet" }));
    const notice = (await screen.findByText("Ayarlar başkası tarafından değiştirildi")).closest('[tabindex="-1"]');
    await waitFor(() => expect(document.activeElement).toBe(notice));
  });
});
