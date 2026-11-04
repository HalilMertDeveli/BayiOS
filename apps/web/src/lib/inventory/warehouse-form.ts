import type { CreateLocationRequest, CreateWarehouseRequest, Location, UpdateLocationRequest, UpdateWarehouseRequest, Warehouse } from "./api";

export const WAREHOUSE_CODE_MAX = 20;
export const WAREHOUSE_NAME_MAX = 100;
export const LOCATION_CODE_MAX = 50;
export const LOCATION_BARCODE_MAX = 64;
export const PICK_SEQUENCE_MAX = 1_000_000;

export type WarehouseFormValues = { code: string; name: string; usesLocations: boolean; active: boolean; makeDefault: boolean };
export type LocationFormValues = { code: string; barcode: string; pickSequence: string; active: boolean };

const hasWhitespace = (s: string) => /\s/.test(s);

export const newWarehouseValues = (): WarehouseFormValues => ({ code: "", name: "", usesLocations: false, active: true, makeDefault: false });

export function warehouseToValues(w: Warehouse): WarehouseFormValues {
  return { code: w.code, name: w.name, usesLocations: w.usesLocations, active: w.status === "active", makeDefault: w.isDefault };
}

/** Mirrors the API rules: code 1–20 without whitespace (create only), name 1–100. */
export function validateWarehouse(values: WarehouseFormValues, mode: "create" | "edit"): Record<string, string> {
  const errors: Record<string, string> = {};
  if (mode === "create") {
    const code = values.code.trim();
    if (!code) errors.code = "Depo kodu girin.";
    else if (code.length > WAREHOUSE_CODE_MAX || hasWhitespace(code)) errors.code = `Boşluk içermeyen, en fazla ${WAREHOUSE_CODE_MAX} karakterlik bir kod girin.`;
  }
  const name = values.name.trim();
  if (!name) errors.name = "Depo adı girin.";
  else if (name.length > WAREHOUSE_NAME_MAX) errors.name = `En fazla ${WAREHOUSE_NAME_MAX} karakter girin.`;
  return errors;
}

export function buildCreateWarehouse(values: WarehouseFormValues): CreateWarehouseRequest {
  return { code: values.code.trim(), name: values.name.trim(), usesLocations: values.usesLocations };
}

/** Merge patch with only the changed members. `isDefault` is only ever sent as true (the API rejects false). */
export function buildWarehousePatch(original: Warehouse, values: WarehouseFormValues): Partial<UpdateWarehouseRequest> {
  const patch: Partial<UpdateWarehouseRequest> = {};
  if (values.name.trim() !== original.name) patch.name = values.name.trim();
  if (values.usesLocations !== original.usesLocations) patch.usesLocations = values.usesLocations;
  const status = values.active ? "active" : "inactive";
  if (status !== original.status) patch.status = status;
  if (values.makeDefault && !original.isDefault) patch.isDefault = true;
  return patch;
}

export const newLocationValues = (): LocationFormValues => ({ code: "", barcode: "", pickSequence: "0", active: true });

export function locationToValues(l: Location): LocationFormValues {
  return { code: l.code, barcode: l.barcode ?? "", pickSequence: String(l.pickSequence), active: l.isActive };
}

export function validateLocation(values: LocationFormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  const code = values.code.trim();
  if (!code) errors.code = "Lokasyon kodu girin (ör. A-01-03).";
  else if (code.length > LOCATION_CODE_MAX || hasWhitespace(code)) errors.code = `Boşluk içermeyen, en fazla ${LOCATION_CODE_MAX} karakterlik bir kod girin.`;
  const barcode = values.barcode.trim();
  if (barcode && (barcode.length > LOCATION_BARCODE_MAX || hasWhitespace(barcode)))
    errors.barcode = `Boşluk içermeyen, en fazla ${LOCATION_BARCODE_MAX} karakterlik bir barkod girin.`;
  const seq = values.pickSequence.trim();
  if (!/^\d+$/.test(seq) || Number(seq) > PICK_SEQUENCE_MAX) errors.pickSequence = "0 ile 1.000.000 arasında bir tam sayı girin.";
  return errors;
}

export function buildCreateLocation(values: LocationFormValues): CreateLocationRequest {
  return { code: values.code.trim(), barcode: values.barcode.trim() || null, pickSequence: Number(values.pickSequence.trim()) };
}

/** Merge patch. A cleared barcode is sent as "" (null would mean "unchanged" to the API). */
export function buildLocationPatch(original: Location, values: LocationFormValues): Partial<UpdateLocationRequest> {
  const patch: Partial<UpdateLocationRequest> = {};
  if (values.code.trim() !== original.code) patch.code = values.code.trim();
  const barcode = values.barcode.trim();
  if (barcode !== (original.barcode ?? "")) patch.barcode = barcode;
  const seq = Number(values.pickSequence.trim());
  if (seq !== Number(original.pickSequence)) patch.pickSequence = seq;
  if (values.active !== original.isActive) patch.isActive = values.active;
  return patch;
}
