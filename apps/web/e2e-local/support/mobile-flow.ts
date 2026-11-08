import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { REPO_ROOT } from "../demo/stack.mjs";

/**
 * Web ↔ mobile real-flow test (#45), shared between its two web phases:
 *
 *   1. web-mobile-1-prepare.spec.ts  — seeds a fresh business through the API (owner + invited warehouse_staff,
 *      shelves, products, stock), then the OWNER creates and approves an order in the web UI (reservation).
 *   2. apps/mobile/integration_test/web_flow_test.dart — the STAFF user on the Android emulator: goods receipt,
 *      no "Düzelt", claims the order's pick task, wrong product / wrong shelf, picks, completes.
 *   3. web-mobile-2-verify.spec.ts   — the owner sees the result on the web (task completed, order packed, stock still
 *      reserved, received balance), packs and ships it; staff permission split checked on the API.
 *
 * Orchestrated by apps/mobile/tool/e2e.sh (locally and in .github/workflows/e2e-mobile.yml). The state file holds the
 * per-run synthetic logins and lives in the git-ignored apps/mobile/build/e2e/ — never print or commit it.
 */
export const FLOW_DIR = process.env.E2E_MOBILE_STATE_DIR ?? join(REPO_ROOT, "apps", "mobile", "build", "e2e");
export const STATE_FILE = join(FLOW_DIR, "state.json");
/** Dart defines for the mobile phase (`--dart-define-from-file`). */
export const DEFINES_FILE = join(FLOW_DIR, "flow.json");

export interface FlowUser {
  email: string;
  password: string;
  fullName: string;
}

export interface FlowProduct {
  sku: string;
  barcode: string;
  name: string;
  variantId: string;
  shelf: string;
  locationId: string;
  /** Stock received by the owner before the order (API). */
  initial: number;
  /** Ordered on the web (0 = not in the order). */
  ordered: number;
}

export interface FlowState {
  stamp: string;
  tenantId: string;
  tenantName: string;
  warehouseId: string;
  owner: FlowUser;
  staff: FlowUser;
  /** A and B are in the order (two shelves, picked in shelf order); C is received on mobile and is the "wrong product". */
  products: { A: FlowProduct; B: FlowProduct; C: FlowProduct };
  /** Units of C the staff user receives on mobile. */
  received: number;
  orderId: string;
  orderNumber: string;
}

export function writeState(state: FlowState) {
  mkdirSync(dirname(STATE_FILE), { recursive: true });
  writeFileSync(STATE_FILE, `${JSON.stringify(state, null, 2)}\n`, { mode: 0o600 });
  const { A, B, C } = state.products;
  const defines: Record<string, string> = {
    BAYIOS_FLOW_EMAIL: state.staff.email,
    BAYIOS_FLOW_PASSWORD: state.staff.password,
    BAYIOS_FLOW_TENANT: state.tenantName,
    BAYIOS_FLOW_ORDER: state.orderNumber,
    BAYIOS_FLOW_RECEIVE_QTY: String(state.received),
  };
  for (const [key, p] of Object.entries({ A, B, C })) {
    defines[`BAYIOS_FLOW_${key}_SKU`] = p.sku;
    defines[`BAYIOS_FLOW_${key}_BARCODE`] = p.barcode;
    defines[`BAYIOS_FLOW_${key}_SHELF`] = p.shelf;
    defines[`BAYIOS_FLOW_${key}_QTY`] = String(p.ordered);
    defines[`BAYIOS_FLOW_${key}_INITIAL`] = String(p.initial);
  }
  // The plain sign-in smoke (integration_test/smoke_test.dart) runs as the same staff user.
  defines.BAYIOS_SMOKE_EMAIL = state.staff.email;
  defines.BAYIOS_SMOKE_PASSWORD = state.staff.password;
  defines.BAYIOS_SMOKE_TENANT = state.tenantName;
  writeFileSync(DEFINES_FILE, `${JSON.stringify(defines, null, 2)}\n`, { mode: 0o600 });
}

export function readState(): FlowState {
  try {
    return JSON.parse(readFileSync(STATE_FILE, "utf8")) as FlowState;
  } catch (error) {
    throw new Error(`No web↔mobile flow state at ${STATE_FILE}: run web-mobile-1-prepare first (apps/mobile/tool/e2e.sh). ${String(error)}`);
  }
}
