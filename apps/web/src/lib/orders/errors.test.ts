import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import {
  addressErrorText,
  isAutoApproved,
  isInsufficientStock,
  isOrderBusy,
  isOrderVersionConflict,
  isPriceMissing,
  isStaleState,
  needsStockConfirmation,
  orderErrorMessage,
  orderFieldErrors,
  readShortages,
  translateOrderFieldMessage,
  unpricedLineIndexes,
} from "./errors";

const apiError = (status: number, problem: Record<string, unknown>) => new ApiError("x", status, problem);

describe("order error classification", () => {
  it("409 order.invalid_transition and order.not_editable mean the view is stale", () => {
    expect(isStaleState(apiError(409, { code: "order.invalid_transition" }))).toBe(true);
    expect(isStaleState(apiError(409, { code: "order.not_editable" }))).toBe(true);
    expect(isStaleState(apiError(409, { code: "order.busy" }))).toBe(false);
  });

  it("412 is a version conflict (reload banner), not a stale state", () => {
    const conflict = apiError(412, { code: "concurrency.version_mismatch" });
    expect(isOrderVersionConflict(conflict)).toBe(true);
    expect(isStaleState(conflict)).toBe(false);
  });

  it("order.busy and inventory.busy are retried once", () => {
    expect(isOrderBusy(apiError(409, { code: "order.busy" }))).toBe(true);
    expect(isOrderBusy(apiError(409, { code: "inventory.busy" }))).toBe(true);
    expect(isOrderBusy(apiError(409, { code: "order.invalid_transition" }))).toBe(false);
    expect(isOrderBusy(new Error("x"))).toBe(false);
  });

  it("insufficient stock: block policy shows shortages, warn policy asks for confirmation", () => {
    const block = apiError(409, {
      code: "inventory.insufficient_stock",
      shortages: [{ variantId: "v1", sku: "LED-1", warehouseId: "w1", locationId: null, requested: 10, available: 3 }],
    });
    const warn = apiError(409, { code: "inventory.insufficient_stock", confirmationRequired: true, shortages: [] });
    expect(isInsufficientStock(block)).toBe(true);
    expect(needsStockConfirmation(block)).toBe(false);
    expect(needsStockConfirmation(warn)).toBe(true);
    expect(readShortages(block)).toEqual([{ variantId: "v1", sku: "LED-1", warehouseId: "w1", locationId: null, requested: 10, available: 3 }]);
  });

  it("order.price_missing lists the unpriced line indexes", () => {
    const error = apiError(422, {
      code: "order.price_missing",
      errors: { "lines[2].unitPrice": ["No price for B."], "lines[0].unitPrice": ["No price for A."], notes: ["x"] },
    });
    expect(isPriceMissing(error)).toBe(true);
    expect(unpricedLineIndexes(error)).toEqual([0, 2]);
    expect(unpricedLineIndexes(new Error("x"))).toEqual([]);
  });
});

describe("orderErrorMessage", () => {
  it("maps order codes to Turkish", () => {
    expect(orderErrorMessage(apiError(409, { code: "order.invalid_transition" }))).toMatch(/şu anki durumunda yapılamaz/);
    expect(orderErrorMessage(apiError(409, { code: "inventory.insufficient_stock" }))).toMatch(/ya hep ya hiç/);
    expect(orderErrorMessage(apiError(422, { code: "order.price_missing" }))).toMatch(/Fiyatı olmayan satırlar/);
  });

  it("maps the dedicated 403 codes (#111) with their extension values, never the title", () => {
    // Titles are the old developer text; they must not change the message.
    const limit = apiError(403, { code: "orders.discount_limit_exceeded", title: "anything", permission: "orders.price.override", limitPercent: 12.5 });
    expect(orderErrorMessage(limit)).toMatch(/^İskonto en fazla %12,5 olabilir./);
    expect(orderErrorMessage(apiError(403, { code: "orders.discount_limit_exceeded" }))).toMatch(/sınırı aşıyor/);
    expect(orderErrorMessage(apiError(403, { code: "orders.manual_price_not_allowed", limitPercent: 10 }))).toMatch(/Manuel birim fiyat giremezsiniz; yalnızca en fazla %10 iskonto/);
    expect(orderErrorMessage(apiError(403, { code: "orders.not_owner", permission: "orders.edit.draft" }))).toMatch(/yalnızca oluşturan kişi/);
    expect(orderErrorMessage(apiError(403, { code: "orders.not_owner", permission: "orders.cancel" }))).toMatch(/kendi oluşturduğunuz siparişleri iptal/);
    expect(orderErrorMessage(apiError(403, { code: "orders.cancel_not_allowed_in_status", orderStatus: "approved" }))).toMatch(/“Onaylandı” durumunda/);
  });

  it("auth.disabled_by_tenant_rule names the switched-off rule by permission; plain auth.forbidden stays generic", () => {
    expect(orderErrorMessage(apiError(403, { code: "auth.disabled_by_tenant_rule", permission: "orders.approve", condition: "tenant_rule.sales_can_approve_orders" }))).toMatch(
      /sipariş onaylaması işletme ayarlarında kapalı/,
    );
    expect(orderErrorMessage(apiError(403, { code: "auth.disabled_by_tenant_rule", permission: "orders.price.override" }))).toMatch(/iskonto sınırı %0/);
    expect(orderErrorMessage(apiError(403, { code: "auth.disabled_by_tenant_rule", permission: "future.permission" }))).toMatch(/ayarlarında rolünüz için kapatılmış/);
    // Before #111 the web parsed this title; it is ignored now.
    expect(orderErrorMessage(apiError(403, { code: "auth.forbidden", title: "orders.price.override: The discount exceeds the sales limit of 10 %" }))).toMatch(/yetkiniz yok/);
  });

  it("submit: an approved result means auto-approval; address 422s are explained per field", () => {
    expect(isAutoApproved("approved")).toBe(true);
    expect(isAutoApproved("pending_approval")).toBe(false);
    const gone = apiError(422, { code: "validation.failed", errors: { shippingAddressId: ["The chosen address no longer exists for this customer; choose another one."] } });
    expect(addressErrorText(gone)).toBe("Teslimat adresi: Seçili adres müşteriden silinmiş. Başka bir adres seçip taslağı kaydedin.");
    expect(addressErrorText(apiError(422, { code: "validation.failed", errors: { notes: ["x"] } }))).toBeNull();
  });

  it("falls back to the generic message for anything else", () => {
    expect(orderErrorMessage(new Error("boom"))).toMatch(/Beklenmeyen/);
  });
});

describe("field messages", () => {
  it("translates the Orders module's validation text", () => {
    expect(translateOrderFieldMessage("Customer is not active.")).toMatch(/aktif değil/);
    expect(translateOrderFieldMessage("Give either discountPercent or discountAmount, not both.")).toMatch(/ya oran ya tutar/);
    expect(translateOrderFieldMessage("The variant's unit does not allow fractions.")).toMatch(/küsurat/);
    expect(translateOrderFieldMessage("Something new.")).toBe("Bu alanı kontrol edin.");
  });

  it("keeps the API's keys", () => {
    const error = apiError(422, { code: "validation.failed", errors: { "lines[1].quantity": ["Must be greater than 0 with at most 3 decimals."] } });
    expect(orderFieldErrors(error)).toEqual({ "lines[1].quantity": "0'dan büyük, en fazla 3 ondalıklı bir miktar girin." });
  });
});
