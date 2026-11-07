// Types for the Playwright specs that drive the demo seeder (seeder.mjs is plain Node ESM so the CLI runs without a
// TypeScript toolchain).
import type { Api, StackConfig } from "./stack.mjs";

export interface DemoOwner {
  email: string;
  password: string;
  fullName: string;
}

export interface DemoVariant {
  id: string;
  productId: string;
  barcode: string | null;
  price: number;
  unit: string;
  category: string;
  name?: string;
}

export interface WhatsappIntent {
  raw: string;
  match: string;
  sku: string;
  quantity: number;
}

export interface DemoCustomer {
  id: string;
  /** Default shipping address. */
  addressId: string;
  /** Default billing address (#136). */
  billingAddressId: string;
  code: string;
  name: string;
  group: string;
}

export interface DemoDefinition {
  code: "motor" | "led";
  sector: string;
  tenant: { name: string; slug: string };
  importProducts: { sku: string; barcode: string; name: string; unit: string; category: string; price: number }[];
  priceLists: { defaultName: string; dealer: { name: string; group: string; discountPercent: number }[] };
  whatsapp: { customer: number; text: string; lines: WhatsappIntent[] };
}

export interface DemoCounts {
  products: number;
  variants: number;
  barcodes: number;
  warehouses: number;
  locations: number;
  priceLists: number;
  dealerGroups: number;
  customers: number;
  importJobs: number;
  whatsappParses: number;
  orders: number;
  ordersByStatus: Record<string, number>;
  partialFulfilment: number;
  /** Orders whose detail carries a billing address (#136). */
  ordersWithBillingAddress: number;
  whatsappOrders: number;
}

export interface SeededDemo {
  skipped: boolean;
  def: DemoDefinition;
  api: Api;
  tenantId: string;
  userId: string;
  warehouses: Record<string, { id: string; name: string; usesLocations: boolean; locations: Record<string, { id: string; code: string }> }>;
  variants: Record<string, DemoVariant>;
  stock: Record<string, { main: number; shop: number }>;
  customers: DemoCustomer[];
  priceLists: { default: string; dealer: Record<string, { id: string; name: string; discountPercent: number }> };
  orders: { target: string; id: string; number: string; status: string; fulfillmentStatus: string }[];
  counts: DemoCounts;
  seconds: number;
}

export const DEMOS: Record<"motor" | "led", (size?: "full" | "small") => DemoDefinition>;

export function seedDemo(p: {
  cfg: StackConfig;
  demo: "motor" | "led";
  size?: "full" | "small";
  owner: DemoOwner;
  tenant?: { name: string; slug: string };
  include?: { importCatalog?: boolean; orders?: boolean; whatsapp?: boolean };
  log?: (line: string) => void;
}): Promise<SeededDemo>;

export function runCatalogImport(
  api: Api,
  def: DemoDefinition,
  priceListId: string,
  log?: (line: string) => void,
): Promise<{ job: { id: string; status: string; progress: { created: number; updated: number; failed: number } }; variants: Record<string, DemoVariant> }>;

export function summarize(api: Api): Promise<DemoCounts>;
