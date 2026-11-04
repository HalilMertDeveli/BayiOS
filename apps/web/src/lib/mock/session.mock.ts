/**
 * MOCK — NOT A REAL SESSION.
 *
 * Authentication (Supabase Auth + API membership check) is not built yet. The shell only needs a
 * role to decide which menu items to show, so this file provides a *preview* role.
 * Nobody is logged in: the UI must say so, and nothing here may be used for authorization —
 * the API is the only place where permissions are enforced (docs/product/roles-permissions.md §1).
 *
 * Replace with the real session once the auth issue lands.
 */
import type { Role } from "@/lib/navigation";

export type PreviewSession = {
  /** Always false until real auth exists. */
  authenticated: false;
  /** Role used only to preview menu visibility. */
  previewRole: Role;
};

export const previewSession: PreviewSession = {
  authenticated: false,
  previewRole: "owner",
};
