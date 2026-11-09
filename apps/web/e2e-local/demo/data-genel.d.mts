// Types for the generic demo dataset (data-genel.mjs is plain Node ESM so the seeder CLI runs without TypeScript).

export type Priority = "low" | "normal" | "high" | "urgent";

export interface GenelVariant {
  sku: string;
  barcode: string;
  name?: string;
  optionValues?: Record<string, string>;
  price: number;
}

export interface GenelProduct {
  name: string;
  category: string;
  brand: string;
  unit: string;
  attributes: { koli_ici?: number; mensei?: string };
  variants: GenelVariant[];
  tiers?: { minQuantity: number; factor: number }[];
}

export interface GenelImportProduct {
  sku: string;
  barcode: string;
  name: string;
  unit: string;
  taxRate: number;
  category: string;
  brand: string;
  price: number;
  attributes: { mensei: string };
}

export interface GenelCustomer {
  code: string;
  name: string;
  contactName: string;
  phone: string;
  email: string;
  group: string;
  address: Record<string, string>;
  billingAddress: Record<string, string>;
}

export interface GenelOrder {
  target: "draft" | "pending" | "approved" | "picking" | "packed" | "partial_closed" | "shipped" | "completed" | "cancelled" | "cancelled_draft";
  customer: number;
  lines: [string, number][];
  priority?: Priority;
  assignTo?: string;
  deliveryInDays?: number;
  notes?: string;
  reason?: string;
  shortage?: { line: number; quantity: number; reason: string };
}

export interface GenelTransfer {
  note: string;
  lines: [string, number][];
  target: "in_transit" | "received" | "closed";
  receive?: number[];
  close?: { resolution: "lost" | "return_to_source"; reason: string };
}

export interface GenelDemo {
  code: "genel";
  sector: "general";
  tenant: { name: string; slug: string };
  owner: { email: string; fullName: string };
  team: { role: string; email: string; fullName: string }[];
  taxRate: number;
  units: { code: string; name: string; allowsFraction: boolean }[];
  categories: string[];
  customFields: { entityType: string; key: string; label: string; dataType: string; unit: string | null; options: string[] | null; filterable: boolean }[];
  warehouses: { code: string; name: string; usesLocations: boolean; locations: { code: string; category: string | null; key?: string; pickSequence: number }[] }[];
  brands: string[];
  products: GenelProduct[];
  importProducts: GenelImportProduct[];
  priceLists: { defaultName: string; dealer: { name: string; group: string; discountPercent: number; tiers: { sku: string; minQuantity: number; factor: number }[] }[] };
  dealerGroups: string[];
  customers: GenelCustomer[];
  stock: (variant: unknown, category: string) => { main: number; shop: number };
  lowStock: string[];
  outOfStock: string[];
  orders: GenelOrder[];
  transfers: GenelTransfer[];
  relocations: [string, number][];
  stockCount: { shelf: string; note: string; counted: number[] };
  whatsapp: { customer: number; text: string; lines: { raw: string; match: string; sku: string; quantity: number }[] };
}

export const GENEL_CATEGORIES: string[];
export const PRIORITIES: Priority[];
export function genelDemo(size?: "full" | "small"): GenelDemo;
