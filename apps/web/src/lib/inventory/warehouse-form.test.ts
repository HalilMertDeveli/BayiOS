import { describe, expect, it } from "vitest";
import type { Location, Warehouse } from "./api";
import {
  buildCreateLocation,
  buildLocationPatch,
  buildWarehousePatch,
  locationToValues,
  validateLocation,
  validateWarehouse,
  warehouseToValues,
} from "./warehouse-form";

const warehouse: Warehouse = {
  id: "wh-1",
  code: "MERKEZ",
  name: "Merkez Depo",
  usesLocations: false,
  isDefault: false,
  status: "active",
  version: 3,
  createdAt: "2026-11-01T00:00:00Z",
  updatedAt: null,
};

const location: Location = {
  id: "loc-1",
  warehouseId: "wh-1",
  code: "A-01",
  barcode: "RAF-A01",
  pickSequence: 10,
  isActive: true,
  version: 1,
  createdAt: "2026-11-01T00:00:00Z",
  updatedAt: null,
};

describe("warehouse form", () => {
  it("validates code (create only) and name", () => {
    expect(validateWarehouse({ code: "ANA DEPO", name: "", usesLocations: false, active: true, makeDefault: false }, "create")).toEqual({
      code: "Boşluk içermeyen, en fazla 20 karakterlik bir kod girin.",
      name: "Depo adı girin.",
    });
    expect(validateWarehouse({ ...warehouseToValues(warehouse), code: "" }, "edit")).toEqual({});
  });

  it("patches only changed members; isDefault only as true", () => {
    const values = warehouseToValues(warehouse);
    expect(buildWarehousePatch(warehouse, values)).toEqual({});
    expect(buildWarehousePatch(warehouse, { ...values, name: " Ana Depo ", usesLocations: true, active: false })).toEqual({
      name: "Ana Depo",
      usesLocations: true,
      status: "inactive",
    });
    expect(buildWarehousePatch(warehouse, { ...values, makeDefault: true })).toEqual({ isDefault: true });
    expect(buildWarehousePatch({ ...warehouse, isDefault: true }, { ...values, makeDefault: false })).toEqual({});
  });
});

describe("location form", () => {
  it("validates code, barcode and pick sequence", () => {
    expect(validateLocation({ code: "", barcode: "A B", pickSequence: "-1", active: true })).toEqual({
      code: "Lokasyon kodu girin (ör. A-01-03).",
      barcode: "Boşluk içermeyen, en fazla 64 karakterlik bir barkod girin.",
      pickSequence: "0 ile 1.000.000 arasında bir tam sayı girin.",
    });
    expect(buildCreateLocation({ code: " A-02 ", barcode: "", pickSequence: "20", active: true })).toEqual({ code: "A-02", barcode: null, pickSequence: 20 });
  });

  it("sends a cleared barcode as an empty string and deactivation as isActive=false", () => {
    const values = locationToValues(location);
    expect(buildLocationPatch(location, { ...values, barcode: "", active: false })).toEqual({ barcode: "", isActive: false });
    expect(buildLocationPatch(location, values)).toEqual({});
  });
});
