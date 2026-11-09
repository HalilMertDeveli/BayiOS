import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { documentErrorMessage, documentFieldErrors, isDocumentBusy, isStaleDocument, movedLineIds, translateDocumentFieldMessage } from "./errors";

const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => new ApiError("x", status, { status, code, ...extra });

describe("stock document errors", () => {
  it("every API code of transfers and counts has a Turkish message", () => {
    const codes = [
      "transfer.invalid_transition",
      "transfer.over_receipt",
      "transfer.unknown_code",
      "transfer.wrong_item",
      "transfer.unknown_location",
      "transfer.warehouse_inactive",
      "transfer.busy",
      "count.invalid_transition",
      "count.already_active",
      "count.below_reserved",
      "count.lines_moved",
      "count.nothing_counted",
      "count.out_of_scope",
      "count.scope_too_large",
      "count.unknown_code",
      "count.unknown_location",
      "count.busy",
      "inventory.insufficient_available",
      "inventory.insufficient_stock",
      "inventory.busy",
    ];
    for (const code of codes) {
      const message = documentErrorMessage(problem(409, code));
      expect(message, code).not.toMatch(/[a-z]+\.[a-z_]+/);
      expect(message, code).not.toBe(problem(409, "unknown").userMessage);
    }
    expect(documentErrorMessage(problem(422, "transfer.over_receipt"))).toContain("mal kabul");
  });

  it("busy, stale and moved lines", () => {
    expect(isDocumentBusy(problem(409, "transfer.busy"))).toBe(true);
    expect(isDocumentBusy(problem(409, "count.busy"))).toBe(true);
    expect(isDocumentBusy(problem(409, "count.already_active"))).toBe(false);
    expect(isStaleDocument(problem(412, "concurrency.version_mismatch"))).toBe(true);
    expect(isStaleDocument(problem(409, "transfer.invalid_transition"))).toBe(true);
    expect(movedLineIds(problem(409, "count.lines_moved", { errors: { "l-1": ["SKU-1: stock moved after the snapshot."], "l-2": ["x"] } }))).toEqual(["l-1", "l-2"]);
    expect(movedLineIds(problem(409, "count.below_reserved"))).toEqual([]);
  });

  it("translates the service validation messages", () => {
    expect(translateDocumentFieldMessage("Source and destination warehouses must differ.")).toContain("farklı olmalı");
    expect(translateDocumentFieldMessage("A destination location is required in this warehouse.")).toContain("Hedef depo");
    expect(translateDocumentFieldMessage("Must be one of: lost, return_to_source.")).toContain("kayıp");
    expect(documentFieldErrors(problem(422, "validation.failed", { errors: { "lines[0].quantity": ["Must be greater than 0 with at most 3 decimals."] } }))).toEqual({
      "lines[0].quantity": "0'dan büyük, en fazla 3 ondalıklı bir miktar girin.",
    });
  });
});
