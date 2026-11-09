import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { ApiError, type ApiClient } from "@/lib/api/client";
import type { Address } from "@/lib/customers/api";
import { customerAddress } from "@/lib/orders/test-fixtures";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { OrderAddressPickers } from "./order-address-pickers";

/** MOCK API: GET /customers/{id}/addresses only (synthetic addresses). */
function fakeApi(byCustomer: Record<string, Address[] | "error">) {
  const paths: string[] = [];
  const api: ApiClient = {
    async request<T>(path: string) {
      paths.push(path);
      const id = /^\/api\/v1\/customers\/([^/]+)\/addresses$/.exec(path)?.[1];
      const items = id ? byCustomer[id] : undefined;
      if (items === "error" || !items) throw new ApiError("x", 500, { code: "server.error" });
      return { data: { items } as T, status: 200, etag: null };
    },
  };
  return { api, paths };
}

function Harness({ customerId, onChange }: { customerId: string | null; onChange: (patch: object) => void }) {
  const [values, setValues] = useState({ shippingAddressId: "", billingAddressId: "" });
  return (
    <OrderAddressPickers
      customerId={customerId}
      shippingAddressId={values.shippingAddressId}
      billingAddressId={values.billingAddressId}
      errors={{}}
      onChange={(patch) => {
        onChange(patch);
        setValues((v) => ({ ...v, ...patch }));
      }}
    />
  );
}

function renderPickers(api: ApiClient, customerId: string | null, onChange = vi.fn()) {
  const view = render(
    <SessionContext value={mockSession("sales", { api })}>
      <Harness customerId={customerId} onChange={onChange} />
    </SessionContext>,
  );
  return { ...view, onChange };
}

describe("OrderAddressPickers (#92)", () => {
  it("asks for a customer first", () => {
    const { api, paths } = fakeApi({});
    renderPickers(api, null);
    expect(screen.getByText(/müşteri seçildikten sonra/)).toBeTruthy();
    expect(paths).toEqual([]);
  });

  it("preselects the customer's default per type and sends another choice by id", async () => {
    const { api } = fakeApi({
      "cus-1": [customerAddress("s1", "shipping", true, "Merkez"), customerAddress("s2", "shipping", false, "Depo"), customerAddress("b1", "billing", true, "Muhasebe")],
    });
    const { onChange } = renderPickers(api, "cus-1");
    const shipping = (await screen.findByRole("combobox", { name: "Teslimat adresi" })) as HTMLSelectElement;
    await vi.waitFor(() => expect(shipping.disabled).toBe(false));
    expect(shipping.value).toBe("");
    expect(shipping.selectedOptions[0].textContent).toBe("Merkez — Merkez Cd. 1, Kadıköy / İstanbul (varsayılan)");
    const billing = screen.getByRole("combobox", { name: "Fatura adresi" }) as HTMLSelectElement;
    expect(billing.selectedOptions[0].textContent).toContain("Muhasebe");
    await userEvent.selectOptions(shipping, "s2");
    expect(onChange).toHaveBeenCalledWith({ shippingAddressId: "s2" });
    expect(shipping.value).toBe("s2");
  });

  it("explains a missing address type and a load failure with retry", async () => {
    const { api } = fakeApi({ "cus-2": [customerAddress("b1", "billing", true, "Muhasebe")], "cus-3": "error" });
    const { rerender } = renderPickers(api, "cus-2");
    expect(await screen.findByText(/teslimat adresi yok; müşteri kartından ekleyebilirsiniz/)).toBeTruthy();
    expect((screen.getByRole("combobox", { name: "Teslimat adresi" }) as HTMLSelectElement).disabled).toBe(true);
    rerender(
      <SessionContext value={mockSession("sales", { api })}>
        <Harness customerId="cus-3" onChange={vi.fn()} />
      </SessionContext>,
    );
    expect(await screen.findByRole("alert")).toHaveProperty("textContent", expect.stringContaining("adresleri yüklenemedi"));
    expect(screen.getByRole("button", { name: "Tekrar dene" })).toBeTruthy();
  });
});
