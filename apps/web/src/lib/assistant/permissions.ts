import type { Access } from "@/lib/session/access";

export type AssistantPermissions = {
  /** whatsapp.parse — every assistant route (paste, review, reject). */
  parse: boolean;
  /** orders.create (unconditional) — needed as well to create the draft from a review (the API checks it on approve). */
  createDraft: boolean;
};

/** Visibility only: the API checks both permissions again. */
export function assistantPermissions(access: Access): AssistantPermissions {
  return { parse: access.canSome("whatsapp.parse"), createDraft: access.can("orders.create") };
}
