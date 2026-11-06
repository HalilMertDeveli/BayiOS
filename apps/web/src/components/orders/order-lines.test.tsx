import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { orderDetail, orderLine } from "@/lib/orders/test-fixtures";
import { cancelReasonText, validateReason } from "./order-actions";
import { LinePrice, OrderTotalsPanel, ShortagesPanel } from "./order-lines";

describe("ShortagesPanel", () => {
  it("lists every short line with requested and available quantities and says nothing was reserved", async () => {
    const onDismiss = vi.fn();
    const lines = [orderLine(), orderLine({ id: "line-2", variantId: "var-2", sku: "LED-2", name: "Panel 36W" })];
    render(
      <ShortagesPanel
        lines={lines}
        onDismiss={onDismiss}
        shortages={[
          { variantId: "var-1", sku: "LED-1", warehouseId: "w", locationId: null, requested: 10, available: 3 },
          { variantId: "var-2", sku: null, warehouseId: "w", locationId: null, requested: 4, available: 0 },
        ]}
      />,
    );
    expect(screen.getByRole("heading", { name: "Onaylanamadı: stok yetersiz" })).toBeTruthy();
    expect(screen.getByText(/hiçbir satıra stok ayrılmadı/)).toBeTruthy();
    const items = within(screen.getByRole("list", { name: "Stoğu yetmeyen satırlar" })).getAllByRole("listitem");
    expect(items.map((i) => i.textContent)).toEqual(["LED-1 — Panel 18W: istenen 10, satılabilir 3", "LED-2 — Panel 36W: istenen 4, satılabilir 0"]);
    await userEvent.click(screen.getByRole("button", { name: "Bu uyarıyı kapat" }));
    expect(onDismiss).toHaveBeenCalled();
  });
});

describe("LinePrice", () => {
  it("shows 'Fiyat yok' for an unpriced line — never 0", () => {
    render(<LinePrice line={orderLine({ priceStatus: "no_price", unitPrice: null, priceSource: null })} currency="TRY" />);
    expect(screen.getByText("Fiyat yok")).toBeTruthy();
    expect(screen.queryByText(/0,00/)).toBeNull();
  });

  it("formats the API's price and names its source", () => {
    render(<LinePrice line={orderLine({ unitPrice: "1234.5", priceSource: "manual" })} currency="TRY" />);
    expect(screen.getByText("₺1.234,50")).toBeTruthy();
    expect(screen.getByText("Manuel fiyat")).toBeTruthy();
  });
});

describe("OrderTotalsPanel", () => {
  it("shows the API totals and VAT breakdown and warns about unpriced lines", () => {
    const { order } = orderDetail();
    render(<OrderTotalsPanel totals={{ ...order.totals!, hasUnpricedLines: true }} currency="TRY" />);
    expect(screen.getByTestId("order-grand-total").textContent).toBe("₺240,00");
    expect(screen.getByText(/KDV %20/)).toBeTruthy();
    expect(screen.getByText("Fiyatsız satırlar toplamlara dahil değil")).toBeTruthy();
  });
});

describe("reason dialogs", () => {
  it("cancel needs a preset; 'Diğer' needs a note", () => {
    expect(validateReason("cancel", "", "")).toMatch(/sebebi seçin/);
    expect(validateReason("cancel", "Diğer", " ")).toMatch(/açıklama/);
    expect(validateReason("cancel", "Stok yok", "")).toBeNull();
    expect(cancelReasonText("Stok yok", " tedarik gecikti ")).toBe("Stok yok: tedarik gecikti");
    expect(cancelReasonText("Mükerrer sipariş", "")).toBe("Mükerrer sipariş");
  });

  it("reject needs a reason of at most 500 characters", () => {
    expect(validateReason("reject", "", "")).toMatch(/Ret sebebini/);
    expect(validateReason("reject", "", "x".repeat(501))).toMatch(/500/);
    expect(validateReason("reject", "", "Fiyat hatalı")).toBeNull();
  });

  it("close-remaining needs a preset; 'Diğer' needs a note (#129)", () => {
    expect(validateReason("close-remaining", "", "")).toMatch(/Kalanı kapatma sebebini/);
    expect(validateReason("close-remaining", "Diğer", "")).toMatch(/açıklama/);
    expect(validateReason("close-remaining", "Stok tedarik edilemiyor", "")).toBeNull();
  });
});
