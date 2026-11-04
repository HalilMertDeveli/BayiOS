import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ResolvedPrice } from "@/lib/pricing/api";
import { PriceCell, SourceCell } from "./price-quote";

function line(overrides: Partial<ResolvedPrice> = {}): ResolvedPrice {
  return {
    variantId: "v1",
    sku: "LED-1",
    quantity: 10,
    status: "priced",
    unitPrice: "12.5",
    source: "dealer_group_price_list",
    rule: "item",
    priceListId: "l1",
    priceListName: "Bayi A",
    priceListItemId: "i1",
    itemPriceListId: "l1",
    minQuantity: 10,
    baseUnitPrice: null,
    discountPercent: null,
    pricesIncludeTax: false,
    taxRate: 20,
    ...overrides,
  };
}

describe("quote result cells", () => {
  it("shows a resolved price formatted in the quote currency with its source and tier", () => {
    render(
      <>
        <PriceCell line={line()} currency="TRY" />
        <SourceCell line={line()} currency="TRY" />
      </>,
    );
    expect(screen.getByText("₺12,50")).toBeTruthy();
    expect(screen.getByText("Bayi grubu listesi: Bayi A")).toBeTruthy();
    expect(screen.getByText(/Liste kalemi · kademe: en az 10/)).toBeTruthy();
  });

  it("shows no_price explicitly — never as 0", () => {
    const { container } = render(<PriceCell line={line({ status: "no_price", unitPrice: null, source: null, rule: null })} currency="TRY" />);
    expect(screen.getByText("Fiyat yok")).toBeTruthy();
    expect(container.textContent).not.toMatch(/0,00/);
  });

  it("keeps a real zero price as ₺0,00", () => {
    render(<PriceCell line={line({ unitPrice: 0 })} currency="TRY" />);
    expect(screen.getByText("₺0,00")).toBeTruthy();
  });

  it("explains a base-list discount", () => {
    render(<SourceCell line={line({ rule: "base_discount", baseUnitPrice: "100", discountPercent: "15", minQuantity: null, source: "default_price_list", priceListName: "Perakende" })} currency="TRY" />);
    expect(screen.getByText("Varsayılan liste: Perakende")).toBeTruthy();
    expect(screen.getByText("Taban liste − iskonto (taban ₺100,00 − %15)")).toBeTruthy();
  });
});
