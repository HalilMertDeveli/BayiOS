import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import type { Address, Customer, DealerGroup } from "./api";
import {
  addressToValues,
  buildAddressPatch,
  buildCreateAddress,
  buildCreateCustomer,
  buildCreateDealerGroup,
  buildCustomerPatch,
  buildDealerGroupPatch,
  confirmsName,
  customerToValues,
  dealerGroupToValues,
  newAddressValues,
  newCustomerValues,
  splitWhatsapp,
  validateAddress,
  validateCustomer,
  validateDealerGroup,
} from "./customer-form";
import { customerErrorMessage, customerFieldErrors } from "./errors";
import { emptyCustomerQuery, parseCustomerQuery, toCustomerApiQuery, toCustomerSearchString } from "./list-query";

function customer(overrides: Partial<Customer> = {}): Customer {
  return {
    id: "c1",
    code: "C-001",
    name: "Örnek Ticaret",
    contactName: null,
    taxNumber: "1234567890",
    taxOffice: null,
    phone: null,
    email: null,
    whatsappNumbers: [],
    dealerGroupId: null,
    priceListId: null,
    paymentTermDays: null,
    creditLimit: null,
    currency: null,
    notes: null,
    status: "active",
    anonymizedAt: null,
    version: 4,
    createdAt: "2026-11-01T00:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

describe("customer form", () => {
  it("mirrors the API's validation rules", () => {
    const errors = validateCustomer({
      ...newCustomerValues(),
      code: "C 1",
      name: "",
      taxNumber: "12345",
      phone: "abc",
      email: "x@",
      whatsappNumbers: "05321234567",
      paymentTermDays: "4000",
      creditLimit: "1.000,50",
    });
    expect(Object.keys(errors).sort()).toEqual(["code", "creditLimit", "email", "name", "paymentTermDays", "phone", "taxNumber", "whatsappNumbers"]);
    expect(validateCustomer({ ...newCustomerValues(), code: "C-1", name: "A", taxNumber: "123 456 789 01", whatsappNumbers: "+90 532 123 45 67" })).toEqual({});
  });

  it("splits WhatsApp numbers by line/comma and strips formatting", () => {
    expect(splitWhatsapp("+90 532 123 45 67\n+90 (555) 000-00-00, ")).toEqual(["+905321234567", "+905550000000"]);
  });

  it("never sends dealer group / price list without pricelists.assign", () => {
    const values = { ...newCustomerValues(), code: "C-1", name: "A", dealerGroupId: "g1", priceListId: "l1", creditLimit: "250000,5", paymentTermDays: "30" };
    expect(buildCreateCustomer(values, true)).toMatchObject({ dealerGroupId: "g1", priceListId: "l1", creditLimit: "250000.5", paymentTermDays: 30 });
    expect(buildCreateCustomer(values, false)).toMatchObject({ dealerGroupId: null, priceListId: null });
  });

  it("patches only changed members (merge patch); cleared fields become null", () => {
    const original = customer({ phone: "0212 000 00 00", creditLimit: 1000, whatsappNumbers: ["+905321234567"], dealerGroupId: "g1" });
    const values = customerToValues(original);
    expect(buildCustomerPatch(original, values, true)).toEqual({});
    expect(buildCustomerPatch(original, { ...values, creditLimit: "1000,00" }, true)).toEqual({});
    expect(buildCustomerPatch(original, { ...values, phone: "", whatsappNumbers: "", status: "inactive", dealerGroupId: "" }, true)).toEqual({
      phone: null,
      whatsappNumbers: [],
      status: "inactive",
      dealerGroupId: null,
    });
    // Without the permission the assignment is left untouched.
    expect(buildCustomerPatch(original, { ...values, dealerGroupId: "" }, false)).toEqual({});
  });

  it("validates and builds addresses; the default is not part of an edit patch", () => {
    expect(Object.keys(validateAddress({ ...newAddressValues(), country: "TUR" })).sort()).toEqual(["city", "country", "line1"]);
    const values = { ...newAddressValues("billing"), line1: "Atatürk Cd. 1", city: "İstanbul", country: "tr", isDefault: true };
    expect(buildCreateAddress(values)).toMatchObject({ type: "billing", isDefault: true, country: "TR", title: null });
    const address: Address = {
      id: "a1",
      customerId: "c1",
      type: "billing",
      isDefault: true,
      title: null,
      line1: "Atatürk Cd. 1",
      line2: null,
      district: null,
      city: "İstanbul",
      postalCode: null,
      country: "TR",
      contactName: null,
      contactPhone: null,
      version: 1,
      createdAt: "",
      updatedAt: null,
    };
    expect(buildAddressPatch(address, addressToValues(address))).toEqual({});
    expect(buildAddressPatch(address, { ...addressToValues(address), district: "Kadıköy" })).toEqual({ district: "Kadıköy" });
  });

  it("dealer groups: percent as text, price list only with pricelists.assign", () => {
    expect(validateDealerGroup({ name: "", priceListId: "", defaultDiscountPercent: "101" })).toEqual({
      name: "Grup adını girin (ör. Bayi A).",
      defaultDiscountPercent: "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin.",
    });
    expect(buildCreateDealerGroup({ name: " Bayi A ", priceListId: "l1", defaultDiscountPercent: "7,5" }, false)).toEqual({
      name: "Bayi A",
      priceListId: null,
      defaultDiscountPercent: "7.5",
    });
    const group: DealerGroup = { id: "g1", name: "Bayi A", priceListId: "l1", defaultDiscountPercent: 5, status: "active", version: 2, createdAt: "", updatedAt: null };
    expect(buildDealerGroupPatch(group, dealerGroupToValues(group), true)).toEqual({});
    expect(buildDealerGroupPatch(group, { ...dealerGroupToValues(group), defaultDiscountPercent: "" }, true)).toEqual({ defaultDiscountPercent: null });
  });

  it("anonymize confirmation needs the exact name (case and spacing tolerant)", () => {
    expect(confirmsName("örnek  TİCARET ", "Örnek Ticaret")).toBe(true);
    expect(confirmsName("Örnek", "Örnek Ticaret")).toBe(false);
    expect(confirmsName("", "")).toBe(false);
  });
});

describe("customer errors", () => {
  it("maps 422 field messages and conflict codes to Turkish", () => {
    const error = new ApiError("x", 422, {
      code: "validation.failed",
      errors: { taxNumber: ["Must be 10 (VKN) or 11 (TCKN) digits."], "whatsappNumbers": ["Each number must be E.164, e.g. +905321234567."] },
    });
    expect(customerFieldErrors(error)).toEqual({
      taxNumber: "Vergi numarası 10 (VKN) veya 11 (TCKN) haneli olmalı.",
      whatsappNumbers: "WhatsApp numaralarını ülke koduyla girin, ör. +905321234567.",
    });
    expect(customerErrorMessage(new ApiError("x", 409, { code: "customers.code_taken" }))).toMatch(/kodu zaten/);
    expect(customerErrorMessage(new ApiError("x", 403, { code: "auth.forbidden" }))).toMatch(/yetkiniz yok/);
  });
});

describe("customer list query", () => {
  it("round-trips the URL and builds the API query", () => {
    const query = parseCustomerQuery(new URLSearchParams("q=Örnek&durum=all&grup=g1"));
    expect(query).toEqual({ q: "Örnek", status: "all", group: "g1" });
    expect(toCustomerSearchString(query)).toBe("q=%C3%96rnek&durum=all&grup=g1");
    expect(new URLSearchParams(toCustomerApiQuery(query, "cur")).toString()).toBe(
      "q=%C3%96rnek&dealerGroupId=g1&status=active%2Cinactive%2Carchived&limit=50&cursor=cur",
    );
    expect(toCustomerApiQuery(emptyCustomerQuery)).toBe("limit=50");
    expect(parseCustomerQuery(new URLSearchParams("durum=bogus")).status).toBe("");
  });
});
