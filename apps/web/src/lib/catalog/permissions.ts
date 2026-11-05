import type { Access } from "@/lib/session/access";

export type CatalogPermissions = {
  /** catalog.write — create, edit and archive products, variants and brands. */
  write: boolean;
  /** catalog.fields.manage — custom fields, categories and units. */
  manageFields: boolean;
};

/** Catalog actions from the effective permissions (GET /me). Visibility only: the API checks every request again. */
export function catalogPermissions(access: Access): CatalogPermissions {
  return {
    write: access.can("catalog.write"),
    manageFields: access.can("catalog.fields.manage"),
  };
}
