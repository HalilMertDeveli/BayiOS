import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import type { PriceList } from "@/lib/pricing/api";
import { newPriceListValues, type PriceListFormValues } from "@/lib/pricing/price-list-form";
import { PriceListFields } from "./price-list-fields";

/** Synthetic price list (invented). */
const base: PriceList = {
  id: "pl-base",
  name: "Perakende",
  kind: "sales",
  currency: "TRY",
  isDefault: true,
  pricesIncludeTax: false,
  validFrom: null,
  validTo: null,
  basePriceListId: null,
  discountPercent: null,
  status: "active",
  version: 1,
  createdAt: "2026-11-01T00:00:00Z",
  updatedAt: null,
};

function Harness() {
  const [values, setValues] = useState<PriceListFormValues>(() => ({ ...newPriceListValues(), name: "Bayi" }));
  return (
    <>
      <PriceListFields values={values} errors={{}} set={(patch) => setValues((v) => ({ ...v, ...patch }))} lists={[base]} original={null} canChooseKind />
      <span data-testid="base">{values.basePriceListId}</span>
    </>
  );
}

describe("PriceListFields — derived list (#96)", () => {
  it("changing the VAT mode clears the base list and says why", async () => {
    render(<Harness />);
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Taban liste" }), "pl-base");
    expect(screen.getByText(/Değiştirirseniz taban liste seçimi kaldırılır/)).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "Fiyatlar KDV dahil" }));
    expect(screen.getByTestId("base").textContent).toBe("");
    expect(screen.getByRole("status").textContent).toBe(
      "KDV modu değiştiği için taban liste seçimi (Perakende) kaldırıldı: taban liste bu listeyle aynı KDV modunda olmalı. Uygun bir taban liste yeniden seçin.",
    );
  });

  it("no notice when no base list was chosen", async () => {
    render(<Harness />);
    await userEvent.click(screen.getByRole("checkbox", { name: "Fiyatlar KDV dahil" }));
    expect(screen.getByRole("status").textContent).toBe("");
  });
});
