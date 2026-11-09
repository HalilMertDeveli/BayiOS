import type { BadgeTone } from "@/components/ui/badge";

export const priceListKindLabels: Record<string, string> = { sales: "Satış", purchase: "Alış" };

export const priceListStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  active: { label: "Aktif", tone: "success" },
  inactive: { label: "Pasif", tone: "neutral" },
  archived: { label: "Arşivlendi", tone: "warning" },
};

/** Where a quoted price came from (ResolvedPrice.source). */
export const priceSourceLabels: Record<string, string> = {
  customer_price_list: "Müşteriye özel liste",
  dealer_group_price_list: "Bayi grubu listesi",
  default_price_list: "Varsayılan liste",
};

/** How it was computed (ResolvedPrice.rule). */
export const priceRuleLabels: Record<string, string> = {
  item: "Liste kalemi",
  base_discount: "Taban liste − iskonto",
};

export const vatModeLabel = (pricesIncludeTax: boolean) => (pricesIncludeTax ? "KDV dahil" : "KDV hariç");
