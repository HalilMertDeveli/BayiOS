import type { ApiClient } from "@/lib/api/client";
import { listProducts } from "@/lib/catalog/api";
import { listBalances, listWarehouses } from "@/lib/inventory/api";
import { listInvitations, listMembers } from "@/lib/members/api";
import { listOrders } from "@/lib/orders/api";
import type { Access } from "@/lib/session/access";
import { readableFacts, type ChecklistKey, type SetupFacts } from "./setup";

/** null when the read is not allowed or fails: an unknown fact is never shown as done (or as missing). */
async function fact(allowed: boolean, read: () => Promise<boolean>): Promise<boolean | null> {
  if (!allowed) return null;
  try {
    return await read();
  } catch {
    return null;
  }
}

/**
 * Setup progress from real data, one small read per fact, all in parallel (first page of one row each):
 * a warehouse exists · a product exists · a stock balance exists (a balance row only appears after a movement) ·
 * someone was invited or another member exists · an order exists.
 */
export async function loadSetupFacts(api: ApiClient, access: Access, signal?: AbortSignal): Promise<SetupFacts> {
  const allowed: Record<ChecklistKey, boolean> = readableFacts(access);
  const [warehouse, products, stock, team, order] = await Promise.all([
    fact(allowed.warehouse, async () => (await listWarehouses(api, { signal })).length > 0),
    fact(allowed.products, async () => (await listProducts(api, "limit=1", signal)).items.length > 0),
    fact(allowed.stock, async () => (await listBalances(api, "limit=1", signal)).items.length > 0),
    fact(allowed.team, async () => {
      const [invitations, members] = await Promise.all([listInvitations(api, null, signal), listMembers(api, { limit: 2 }, signal)]);
      return invitations.length > 0 || members.items.length > 1;
    }),
    fact(allowed.order, async () => (await listOrders(api, "limit=1", signal)).items.length > 0),
  ]);
  return { warehouse, products, stock, team, order };
}
