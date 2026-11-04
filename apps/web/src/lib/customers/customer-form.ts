import { parseDecimalInput, sameDecimal, toDecimalInput } from "@/lib/pricing/money";
import { parsePercent } from "@/lib/pricing/price-list-form";
import type {
  Address,
  CreateAddressRequest,
  CreateCustomerRequest,
  CreateDealerGroupRequest,
  Customer,
  DealerGroup,
  UpdateAddressRequest,
  UpdateCustomerRequest,
  UpdateDealerGroupRequest,
} from "./api";

export const CODE_MAX = 30;
export const NAME_MAX = 200;
export const SHORT_TEXT_MAX = 100;
export const NOTES_MAX = 2000;
export const MAX_WHATSAPP = 5;
export const PAYMENT_TERM_MAX = 3650;

const PHONE = /^\+?[0-9 ()-]{7,20}$/;
const E164 = /^\+[1-9]\d{6,14}$/;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

// ---- Customer ----

export type CustomerFormValues = {
  code: string;
  name: string;
  contactName: string;
  taxNumber: string;
  taxOffice: string;
  phone: string;
  email: string;
  /** One number per line (or comma separated). */
  whatsappNumbers: string;
  dealerGroupId: string;
  priceListId: string;
  paymentTermDays: string;
  creditLimit: string;
  /** "" = the tenant's currency. */
  currency: string;
  notes: string;
  status: "active" | "inactive";
};

export const newCustomerValues = (): CustomerFormValues => ({
  code: "",
  name: "",
  contactName: "",
  taxNumber: "",
  taxOffice: "",
  phone: "",
  email: "",
  whatsappNumbers: "",
  dealerGroupId: "",
  priceListId: "",
  paymentTermDays: "",
  creditLimit: "",
  currency: "",
  notes: "",
  status: "active",
});

export function customerToValues(c: Customer): CustomerFormValues {
  return {
    code: c.code,
    name: c.name,
    contactName: c.contactName ?? "",
    taxNumber: c.taxNumber ?? "",
    taxOffice: c.taxOffice ?? "",
    phone: c.phone ?? "",
    email: c.email ?? "",
    whatsappNumbers: c.whatsappNumbers.join("\n"),
    dealerGroupId: c.dealerGroupId ?? "",
    priceListId: c.priceListId ?? "",
    paymentTermDays: c.paymentTermDays === null ? "" : String(c.paymentTermDays),
    creditLimit: toDecimalInput(c.creditLimit),
    currency: c.currency ?? "",
    notes: c.notes ?? "",
    status: c.status === "inactive" ? "inactive" : "active",
  };
}

export function splitWhatsapp(text: string): string[] {
  return text
    .split(/[\n,;]+/)
    .map((s) => s.replace(/[\s()-]/g, ""))
    .filter(Boolean);
}

/** Tax number without spaces (VKN 10 / TCKN 11 digits). */
const compactTax = (text: string) => text.replace(/\s+/g, "");

/** Mirrors the API rules (CustomerService); the API re-validates and answers 422 per field. */
export function validateCustomer(values: CustomerFormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  const code = values.code.trim();
  if (!code) errors.code = "Müşteri kodu girin (ör. C-001).";
  else if (code.length > CODE_MAX || /\s/.test(code)) errors.code = `Boşluk içermeyen, en fazla ${CODE_MAX} karakterlik bir kod girin.`;
  const name = values.name.trim();
  if (!name) errors.name = "Müşteri (firma) adını girin.";
  else if (name.length > NAME_MAX) errors.name = `En fazla ${NAME_MAX} karakter girin.`;
  if (values.contactName.trim().length > SHORT_TEXT_MAX) errors.contactName = `En fazla ${SHORT_TEXT_MAX} karakter girin.`;
  const tax = compactTax(values.taxNumber);
  if (tax && !/^\d{10,11}$/.test(tax)) errors.taxNumber = "Vergi numarası 10 (VKN) veya 11 (TCKN) haneli olmalı.";
  if (values.taxOffice.trim().length > SHORT_TEXT_MAX) errors.taxOffice = `En fazla ${SHORT_TEXT_MAX} karakter girin.`;
  const phone = values.phone.trim();
  if (phone && !PHONE.test(phone)) errors.phone = "Geçerli bir telefon girin (7–20 karakter; rakam, boşluk, tire, parantez, başta +).";
  const email = values.email.trim();
  if (email && (email.length > 254 || !EMAIL.test(email))) errors.email = "Geçerli bir e-posta adresi girin.";
  const whatsapp = splitWhatsapp(values.whatsappNumbers);
  if (whatsapp.length > MAX_WHATSAPP) errors.whatsappNumbers = `En fazla ${MAX_WHATSAPP} WhatsApp numarası girilebilir.`;
  else if (whatsapp.some((n) => !E164.test(n))) errors.whatsappNumbers = "WhatsApp numaralarını ülke koduyla girin, ör. +905321234567.";
  const term = values.paymentTermDays.trim();
  if (term && (!/^\d+$/.test(term) || Number(term) > PAYMENT_TERM_MAX)) errors.paymentTermDays = "Vade 0 ile 3650 gün arasında bir tam sayı olmalı.";
  if (values.creditLimit.trim() && !parseDecimalInput(values.creditLimit, 4).ok)
    errors.creditLimit = "0 veya daha büyük, en fazla 4 ondalıklı bir tutar girin (binlik ayırıcı kullanmayın).";
  if (values.notes.length > NOTES_MAX) errors.notes = `En fazla ${NOTES_MAX} karakter girin.`;
  return errors;
}

const textOrNull = (s: string) => s.trim() || null;

function creditLimitOf(values: CustomerFormValues): string | null {
  const parsed = parseDecimalInput(values.creditLimit, 4);
  return values.creditLimit.trim() && parsed.ok ? parsed.value : null;
}

/** Create body. Without pricelists.assign the group and price list are never sent (the API would answer 403). */
export function buildCreateCustomer(values: CustomerFormValues, canAssign: boolean): CreateCustomerRequest {
  const term = values.paymentTermDays.trim();
  return {
    code: values.code.trim(),
    name: values.name.trim(),
    contactName: textOrNull(values.contactName),
    taxNumber: compactTax(values.taxNumber) || null,
    taxOffice: textOrNull(values.taxOffice),
    phone: textOrNull(values.phone),
    email: textOrNull(values.email),
    whatsappNumbers: splitWhatsapp(values.whatsappNumbers),
    dealerGroupId: canAssign ? values.dealerGroupId || null : null,
    priceListId: canAssign ? values.priceListId || null : null,
    paymentTermDays: term ? Number(term) : null,
    creditLimit: creditLimitOf(values),
    currency: values.currency || null,
    notes: textOrNull(values.notes),
  };
}

/** Merge patch with the changed members only; a cleared field is sent as null (whatsapp as []). */
export function buildCustomerPatch(original: Customer, values: CustomerFormValues, canAssign: boolean): Partial<UpdateCustomerRequest> {
  const patch: Partial<UpdateCustomerRequest> = {};
  const text = (key: "code" | "name" | "contactName" | "taxOffice" | "phone" | "email" | "notes", next: string | null) => {
    if ((original[key] ?? null) !== next) patch[key] = next;
  };
  text("code", values.code.trim());
  text("name", values.name.trim());
  text("contactName", textOrNull(values.contactName));
  text("taxOffice", textOrNull(values.taxOffice));
  text("phone", textOrNull(values.phone));
  text("email", textOrNull(values.email));
  text("notes", textOrNull(values.notes));
  const tax = compactTax(values.taxNumber) || null;
  if (tax !== (original.taxNumber ?? null)) patch.taxNumber = tax;
  const whatsapp = splitWhatsapp(values.whatsappNumbers);
  if (whatsapp.join(",") !== original.whatsappNumbers.join(",")) patch.whatsappNumbers = whatsapp;
  if (canAssign) {
    if ((values.dealerGroupId || null) !== (original.dealerGroupId ?? null)) patch.dealerGroupId = values.dealerGroupId || null;
    if ((values.priceListId || null) !== (original.priceListId ?? null)) patch.priceListId = values.priceListId || null;
  }
  const term = values.paymentTermDays.trim() ? Number(values.paymentTermDays.trim()) : null;
  if (term !== (original.paymentTermDays === null ? null : Number(original.paymentTermDays))) patch.paymentTermDays = term;
  const credit = creditLimitOf(values);
  if (!sameDecimal(credit, original.creditLimit)) patch.creditLimit = credit;
  if ((values.currency || null) !== (original.currency ?? null)) patch.currency = values.currency || null;
  if (values.status !== original.status && original.status !== "archived") patch.status = values.status;
  return patch;
}

// ---- Address ----

export const ADDRESS_TYPES = ["shipping", "billing"] as const;
export type AddressType = (typeof ADDRESS_TYPES)[number];
export const addressTypeLabels: Record<string, string> = { shipping: "Teslimat", billing: "Fatura" };

export type AddressFormValues = {
  type: AddressType;
  title: string;
  line1: string;
  line2: string;
  district: string;
  city: string;
  postalCode: string;
  country: string;
  contactName: string;
  contactPhone: string;
  /** Create only: make it the default of its type (the first address of a type always becomes the default). */
  isDefault: boolean;
};

export const newAddressValues = (type: AddressType = "shipping"): AddressFormValues => ({
  type,
  title: "",
  line1: "",
  line2: "",
  district: "",
  city: "",
  postalCode: "",
  country: "TR",
  contactName: "",
  contactPhone: "",
  isDefault: false,
});

export function addressToValues(a: Address): AddressFormValues {
  return {
    type: a.type === "billing" ? "billing" : "shipping",
    title: a.title ?? "",
    line1: a.line1,
    line2: a.line2 ?? "",
    district: a.district ?? "",
    city: a.city,
    postalCode: a.postalCode ?? "",
    country: a.country,
    contactName: a.contactName ?? "",
    contactPhone: a.contactPhone ?? "",
    isDefault: a.isDefault,
  };
}

export function validateAddress(values: AddressFormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  const line1 = values.line1.trim();
  if (!line1) errors.line1 = "Adres satırını girin (mahalle, cadde, no).";
  else if (line1.length > NAME_MAX) errors.line1 = `En fazla ${NAME_MAX} karakter girin.`;
  if (values.line2.trim().length > NAME_MAX) errors.line2 = `En fazla ${NAME_MAX} karakter girin.`;
  const city = values.city.trim();
  if (!city) errors.city = "İl girin.";
  else if (city.length > SHORT_TEXT_MAX) errors.city = `En fazla ${SHORT_TEXT_MAX} karakter girin.`;
  for (const key of ["title", "district", "contactName"] as const) {
    if (values[key].trim().length > SHORT_TEXT_MAX) errors[key] = `En fazla ${SHORT_TEXT_MAX} karakter girin.`;
  }
  if (values.postalCode.trim().length > 20) errors.postalCode = "En fazla 20 karakter girin.";
  if (!/^[A-Za-z]{2}$/.test(values.country.trim())) errors.country = "Ülkeyi iki harfli kodla girin (ör. TR).";
  const phone = values.contactPhone.trim();
  if (phone && !PHONE.test(phone)) errors.contactPhone = "Geçerli bir telefon girin (7–20 karakter; rakam, boşluk, tire, parantez, başta +).";
  return errors;
}

export function buildCreateAddress(values: AddressFormValues): CreateAddressRequest {
  return {
    type: values.type,
    isDefault: values.isDefault,
    title: textOrNull(values.title),
    line1: values.line1.trim(),
    line2: textOrNull(values.line2),
    district: textOrNull(values.district),
    city: values.city.trim(),
    postalCode: textOrNull(values.postalCode),
    country: values.country.trim().toUpperCase(),
    contactName: textOrNull(values.contactName),
    contactPhone: textOrNull(values.contactPhone),
  };
}

/** Merge patch without `isDefault` (the default is switched with its own action). */
export function buildAddressPatch(original: Address, values: AddressFormValues): Partial<UpdateAddressRequest> {
  const next = buildCreateAddress(values);
  const patch: Partial<UpdateAddressRequest> = {};
  const keys = ["type", "title", "line1", "line2", "district", "city", "postalCode", "country", "contactName", "contactPhone"] as const;
  for (const key of keys) {
    if ((next[key] ?? null) !== (original[key] ?? null)) patch[key] = next[key];
  }
  return patch;
}

// ---- Dealer group ----

export type DealerGroupFormValues = { name: string; priceListId: string; defaultDiscountPercent: string };

export const newDealerGroupValues = (): DealerGroupFormValues => ({ name: "", priceListId: "", defaultDiscountPercent: "" });

export function dealerGroupToValues(g: DealerGroup): DealerGroupFormValues {
  return { name: g.name, priceListId: g.priceListId ?? "", defaultDiscountPercent: toDecimalInput(g.defaultDiscountPercent) };
}

export function validateDealerGroup(values: DealerGroupFormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  const name = values.name.trim();
  if (!name) errors.name = "Grup adını girin (ör. Bayi A).";
  else if (name.length > SHORT_TEXT_MAX) errors.name = `En fazla ${SHORT_TEXT_MAX} karakter girin.`;
  if (values.defaultDiscountPercent.trim() && parsePercent(values.defaultDiscountPercent) === null)
    errors.defaultDiscountPercent = "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin.";
  return errors;
}

export function buildCreateDealerGroup(values: DealerGroupFormValues, canAssign: boolean): CreateDealerGroupRequest {
  return {
    name: values.name.trim(),
    priceListId: canAssign ? values.priceListId || null : null,
    defaultDiscountPercent: values.defaultDiscountPercent.trim() ? parsePercent(values.defaultDiscountPercent) : null,
  };
}

export function buildDealerGroupPatch(original: DealerGroup, values: DealerGroupFormValues, canAssign: boolean): Partial<UpdateDealerGroupRequest> {
  const patch: Partial<UpdateDealerGroupRequest> = {};
  if (values.name.trim() !== original.name) patch.name = values.name.trim();
  if (canAssign && (values.priceListId || null) !== (original.priceListId ?? null)) patch.priceListId = values.priceListId || null;
  const discount = values.defaultDiscountPercent.trim() ? parsePercent(values.defaultDiscountPercent) : null;
  if (!sameDecimal(discount, original.defaultDiscountPercent)) patch.defaultDiscountPercent = discount;
  return patch;
}

/** Shown in the anonymize confirmation: the user must type the customer's name exactly (case/space tolerant). */
export function confirmsName(typed: string, name: string): boolean {
  const norm = (s: string) => s.trim().replace(/\s+/g, " ").toLocaleLowerCase("tr");
  return norm(typed) !== "" && norm(typed) === norm(name);
}
