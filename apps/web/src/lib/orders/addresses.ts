import type { SelectOption } from "@/components/ui/select";
import type { Address } from "@/lib/customers/api";
import type { components } from "@/lib/api/schema";

/** Shipping / billing address on an order: a live preview on drafts, a frozen snapshot from submit on (#92). */
export type OrderAddress = components["schemas"]["OrderAddressResponse"];

export type OrderAddressType = "shipping" | "billing";

/** The draft's address members (API `shippingAddressId` / `billingAddressId`); null = the customer's default. */
export const ADDRESS_FIELD: Record<OrderAddressType, "shippingAddressId" | "billingAddressId"> = {
  shipping: "shippingAddressId",
  billing: "billingAddressId",
};

/** One line for a picker: "Merkez — Örnek Mah. 1, Kadıköy / İstanbul". */
export function addressLabel(a: Pick<Address, "title" | "line1" | "district" | "city">): string {
  const place = [a.district, a.city].filter(Boolean).join(" / ");
  const text = [a.line1, place].filter(Boolean).join(", ");
  return a.title ? `${a.title} — ${text}` : text;
}

/** line1 of an order address masked after the customer was anonymized (KVKK, #115); district/city/country are kept. */
export const MASKED_ADDRESS_LINE = "Silinmiş adres";

export function isMaskedAddress(a: Pick<OrderAddress, "line1">): boolean {
  return a.line1 === MASKED_ADDRESS_LINE;
}

/** Multi-line text of an address snapshot for display (no empty lines). */
export function addressLines(a: OrderAddress): string[] {
  const place = [a.postalCode, a.district, a.city].filter(Boolean).join(" ");
  const contact = [a.contactName, a.contactPhone].filter(Boolean).join(" · ");
  return [a.title, a.line1, a.line2, [place, a.country].filter(Boolean).join(", "), contact].filter((x): x is string => Boolean(x));
}

export type AddressChoice = {
  options: SelectOption[];
  /** The customer's default address of this type, if any. */
  defaultAddress: Address | null;
  /** The chosen id is not among the customer's addresses of this type (removed meanwhile): submit would fail with 422. */
  missing: boolean;
  /** No address of this type at all. */
  empty: boolean;
};

/**
 * Options of an address picker. Value "" = "the customer's default" (sent as null, resolved by the API at submit), shown
 * with the default address's text so it reads as preselected; the other addresses of the type follow. A chosen id that
 * is the default (pinned) or no longer exists keeps its own option so the select never shows something else.
 */
export function addressChoice(addresses: readonly Address[], type: OrderAddressType, value: string): AddressChoice {
  const ofType = addresses.filter((a) => a.type === type);
  const defaultAddress = ofType.find((a) => a.isDefault) ?? null;
  const options: SelectOption[] = [
    { value: "", label: defaultAddress ? `${addressLabel(defaultAddress)} (varsayılan)` : "Varsayılan adres yok" },
    ...ofType.filter((a) => !a.isDefault).map((a) => ({ value: a.id, label: addressLabel(a) })),
  ];
  let missing = false;
  if (value && !options.some((o) => o.value === value)) {
    if (defaultAddress && defaultAddress.id === value) {
      options.push({ value, label: `${addressLabel(defaultAddress)} (bu adres sabit)` });
    } else {
      missing = true;
      options.push({ value, label: "Seçili adres artık yok" });
    }
  }
  return { options, defaultAddress, missing, empty: ofType.length === 0 };
}
