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
import { BusinessRules } from "./business-rules";

vi.mock("next/link", () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

/** Synthetic settings (MOCK). */
const SETTINGS: TenantSettings = {
  negativeStockPolicy: "block",
  salesCanApproveOrders: true,
  salesMaxDiscountPercent: 0,
  showCostToManager: false,
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

function fakeApi(patchError?: ApiError) {
  const calls: Call[] = [];
  let current = { ...SETTINGS };
  const api: ApiClient = {
    async request<T>(path: string, opts: RequestOptions = {}) {
      calls.push({ path, options: opts });
      if (opts.method === "PATCH") {
        if (patchError) throw patchError;
        current = { ...current, ...(opts.body as object) };
        return { data: current as T, status: 200, etag: 'W/"2"' };
      }
      return { data: current as T, status: 200, etag: 'W/"1"' };
    },
  };
  return { api, calls };
}

function renderRules(role: FixtureRole, api: ApiClient, reload = vi.fn(async () => [])) {
  render(
    <ToastProvider>
      <SessionContext value={mockSession(role, { api, reload })}>
        <BusinessRules />
      </SessionContext>
    </ToastProvider>,
  );
  return reload;
}

describe("BusinessRules", () => {
  it("saves only the changed rules with If-Match and refreshes /me", async () => {
    const { api, calls } = fakeApi();
    const reload = renderRules("owner", api);
    await userEvent.click(await screen.findByRole("checkbox", { name: "Satış personeli sipariş onaylayabilir" }));
    const discount = screen.getByRole("textbox", { name: /iskonto sınırı/ });
    await userEvent.clear(discount);
    await userEvent.type(discount, "5");
    expect(screen.getByText(/en fazla %5 iskonto/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Kuralları kaydet" }));
    expect(await screen.findByText("İş kuralları kaydedildi")).toBeTruthy();
    const patch = calls.find((c) => c.options.method === "PATCH");
    expect(patch?.options.ifMatch).toBe('W/"1"');
    expect(patch?.options.contentType).toBe("application/merge-patch+json");
    expect(patch?.options.body).toEqual({ salesCanApproveOrders: false, salesMaxDiscountPercent: 5 });
    expect(reload).toHaveBeenCalled();
  });

  it("2FA is labelled as not enforced", async () => {
    renderRules("admin", fakeApi().api);
    expect(await screen.findByText("Yakında · henüz uygulanmıyor")).toBeTruthy();
    expect(screen.getByText(/ek koruma sağlamaz/)).toBeTruthy();
  });

  it("local validation blocks an out-of-range discount", async () => {
    const { api, calls } = fakeApi();
    renderRules("owner", api);
    const discount = await screen.findByRole("textbox", { name: /iskonto sınırı/ });
    await userEvent.clear(discount);
    await userEvent.type(discount, "150");
    await userEvent.click(screen.getByRole("button", { name: "Kuralları kaydet" }));
    expect(await screen.findByText("Bazı alanlar hatalı. İşaretli alanları düzeltin.")).toBeTruthy();
    expect(calls.some((c) => c.options.method === "PATCH")).toBe(false);
  });

  it("422 lands on the field in Turkish", async () => {
    const { api } = fakeApi(new ApiError("x", 422, { code: "validation.failed", errors: { salesMaxDiscountPercent: ["Must be between 0 and 100."] } }));
    renderRules("owner", api);
    const discount = await screen.findByRole("textbox", { name: /iskonto sınırı/ });
    await userEvent.clear(discount);
    await userEvent.type(discount, "9");
    await userEvent.click(screen.getByRole("button", { name: "Kuralları kaydet" }));
    expect((await screen.findAllByText(/0 ile 100 arasında, en fazla 2 ondalıklı/)).length).toBeGreaterThan(0);
  });

  it("412 asks to reload the current rules", async () => {
    const { api } = fakeApi(new ApiError("x", 412, { code: "concurrency.version_mismatch" }));
    renderRules("owner", api);
    await userEvent.click(await screen.findByRole("checkbox", { name: "Depo personeli barkod atayabilir" }));
    await userEvent.click(screen.getByRole("button", { name: "Kuralları kaydet" }));
    expect(await screen.findByText("Kurallar başkası tarafından değiştirildi")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Güncel kuralları yükle" })).toBeTruthy();
  });

  it("a failed save focuses the error even when a frame runs before the update commits (#152)", async () => {
    frameBeforeCommit();
    try {
      const { api } = fakeApi(new ApiError("x", 500, { code: "server.error" }));
      renderRules("owner", api);
      await userEvent.click(await screen.findByRole("checkbox", { name: "Depo personeli barkod atayabilir" }));
      await userEvent.click(screen.getByRole("button", { name: "Kuralları kaydet" }));
      const alert = (await screen.findByText("Kurallar kaydedilemedi")).closest('[tabindex="-1"]');
      await waitFor(() => expect(document.activeElement).toBe(alert));
    } finally {
      vi.restoreAllMocks();
    }
  });

  it("412: the reload notice takes focus (the save button it came from is disabled now)", async () => {
    const { api } = fakeApi(new ApiError("x", 412, { code: "concurrency.version_mismatch" }));
    renderRules("owner", api);
    await userEvent.click(await screen.findByRole("checkbox", { name: "Depo personeli barkod atayabilir" }));
    await userEvent.click(screen.getByRole("button", { name: "Kuralları kaydet" }));
    const notice = (await screen.findByText("Kurallar başkası tarafından değiştirildi")).closest('[tabindex="-1"]');
    await waitFor(() => expect(document.activeElement).toBe(notice));
  });

  it.each(["manager", "sales", "viewer"] as const)("%s: locked, no request", async (role) => {
    const { api, calls } = fakeApi();
    renderRules(role, api);
    expect(await screen.findByText("İş kurallarını değiştirme yetkiniz yok")).toBeTruthy();
    expect(calls).toHaveLength(0);
  });
});
