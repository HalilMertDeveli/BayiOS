import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import {
  FULFILMENT_MESSAGES,
  SCAN_REJECTIONS,
  fulfilmentErrorMessage,
  isFulfilmentBusy,
  isScanRejection,
  isStaleFulfilment,
  isVersionConflict,
  mismatchedLineIds,
} from "./errors";

const err = (status: number, code: string, extra: Record<string, unknown> = {}) => new ApiError("x", status, { code, ...extra });

describe("fulfilment errors", () => {
  it("has a Turkish message for every scan rejection and the contract's conflict codes", () => {
    for (const code of [...SCAN_REJECTIONS, "picking.already_assigned", "picking.not_assignee", "shipment.already_confirmed", "order.busy", "inventory.busy", "picking.busy"]) {
      expect(FULFILMENT_MESSAGES[code], code).toBeTruthy();
      expect(fulfilmentErrorMessage(err(422, code))).toBe(FULFILMENT_MESSAGES[code]);
    }
    expect(fulfilmentErrorMessage(err(422, "picking.wrong_location"))).toMatch(/Yanlış raf/);
    expect(fulfilmentErrorMessage(err(422, "picking.over_pick"))).toMatch(/Fazla toplama/);
  });

  it("classifies busy, stale, version and scan rejections", () => {
    expect(isFulfilmentBusy(err(409, "order.busy"))).toBe(true);
    expect(isFulfilmentBusy(err(409, "inventory.busy"))).toBe(true);
    expect(isFulfilmentBusy(err(409, "picking.busy"))).toBe(true);
    expect(isFulfilmentBusy(err(409, "picking.already_assigned"))).toBe(false);
    expect(isStaleFulfilment(err(409, "picking.invalid_transition"))).toBe(true);
    expect(isVersionConflict(new ApiError("x", 412, null))).toBe(true);
    expect(isScanRejection(err(422, "picking.wrong_item"))).toBe(true);
    expect(isScanRejection(err(422, "validation.failed"))).toBe(false);
    expect(isScanRejection(new Error("x"))).toBe(false);
  });

  it("translates validation messages and falls back to the generic text", () => {
    expect(fulfilmentErrorMessage(err(422, "validation.failed", { errors: { quantity: ["The item's unit does not allow fractions."] } }))).toMatch(/küsurat/);
    expect(fulfilmentErrorMessage(new ApiError("x", 404, { code: "resource.not_found" }))).toMatch(/bulunamadı/);
    expect(fulfilmentErrorMessage(new ApiError("x", 0, null, "Sunucuya ulaşılamadı."))).toBe("Sunucuya ulaşılamadı.");
    expect(fulfilmentErrorMessage("boom")).toMatch(/Beklenmeyen/);
  });

  it("reads the mismatched shipment lines of a pack-check rejection", () => {
    expect(mismatchedLineIds(err(422, "packing.quantity_mismatch", { errors: { s1: ["MOCK: ships 2, packed 1."] } }))).toEqual(["s1"]);
    expect(mismatchedLineIds(err(422, "validation.failed", { errors: { s1: ["x"] } }))).toEqual([]);
  });
});
