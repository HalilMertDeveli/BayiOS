import type { Brand, Category, CreateCategoryRequest, CreateUnitRequest, Unit, UpdateCategoryRequest, UpdateUnitRequest } from "./api";

/**
 * Client checks for brands, units and categories — the same limits the API enforces (ReferenceDataService): brand and
 * category names 1–100 characters, unit names 1–50, unit codes 1–20 characters without whitespace (stored lower case,
 * fixed after creation), category depth ≤ 4 with a fixed parent. The API checks everything again.
 */
export const BRAND_NAME_MAX = 100;
export const CATEGORY_NAME_MAX = 100;
export const UNIT_NAME_MAX = 50;
export const UNIT_CODE_MAX = 20;
export const CATEGORY_MAX_DEPTH = 4;
const SORT_MIN = -1_000_000;
const SORT_MAX = 1_000_000;

export type FieldErrors = Record<string, string>;

/** Trimmed, inner whitespace collapsed — how the API cleans names (CatalogText.Clean). */
export function cleanName(value: string): string {
  return value.trim().replace(/\s+/g, " ");
}

function nameError(value: string, max: number): string | null {
  const name = cleanName(value);
  if (!name) return "Bu alan zorunlu.";
  if (name.length > max) return `En fazla ${max} karakter girin.`;
  return null;
}

export function validateBrandName(value: string): FieldErrors {
  const error = nameError(value, BRAND_NAME_MAX);
  return error ? { name: error } : {};
}

export type UnitFormValues = { code: string; name: string; allowsFraction: boolean };

export function validateUnit(values: UnitFormValues, mode: "create" | "edit"): FieldErrors {
  const errors: FieldErrors = {};
  if (mode === "create") {
    const code = values.code.trim();
    if (!code) errors.code = "Bu alan zorunlu.";
    else if (/\s/.test(code) || code.length > UNIT_CODE_MAX) errors.code = `Boşluk içermeyen 1–${UNIT_CODE_MAX} karakter girin.`;
  }
  const name = nameError(values.name, UNIT_NAME_MAX);
  if (name) errors.name = name;
  return errors;
}

export function buildUnitCreate(values: UnitFormValues): CreateUnitRequest {
  return { code: values.code.trim(), name: cleanName(values.name), allowsFraction: values.allowsFraction };
}

/** Changed members only; empty when nothing changed. */
export function buildUnitPatch(values: UnitFormValues, original: Unit): Partial<UpdateUnitRequest> {
  const patch: Partial<UpdateUnitRequest> = {};
  if (cleanName(values.name) !== original.name) patch.name = cleanName(values.name);
  if (values.allowsFraction !== original.allowsFraction) patch.allowsFraction = values.allowsFraction;
  return patch;
}

export type CategoryFormValues = { name: string; sortOrder: string };

/** "" → 0; otherwise an integer the API accepts. */
export function parseSortOrder(text: string): number | null {
  const value = text.trim();
  if (!value) return 0;
  if (!/^-?\d+$/.test(value)) return null;
  const n = Number(value);
  return n >= SORT_MIN && n <= SORT_MAX ? n : null;
}

export function validateCategory(values: CategoryFormValues): FieldErrors {
  const errors: FieldErrors = {};
  const name = nameError(values.name, CATEGORY_NAME_MAX);
  if (name) errors.name = name;
  if (parseSortOrder(values.sortOrder) === null) errors.sortOrder = "Tam sayı girin (boş = 0).";
  return errors;
}

export function buildCategoryCreate(values: CategoryFormValues, parentId: string | null): CreateCategoryRequest {
  return { name: cleanName(values.name), parentId, sortOrder: parseSortOrder(values.sortOrder) ?? 0 };
}

export function buildCategoryPatch(values: CategoryFormValues, original: Category): Partial<UpdateCategoryRequest> {
  const patch: Partial<UpdateCategoryRequest> = {};
  if (cleanName(values.name) !== original.name) patch.name = cleanName(values.name);
  const sort = parseSortOrder(values.sortOrder) ?? 0;
  if (sort !== Number(original.sortOrder)) patch.sortOrder = sort;
  return patch;
}

export type CategoryNode = {
  category: Category;
  /** 1 for a root, computed from the parent links (not from the API's `depth`/`path`). */
  level: number;
  /** Active (non-archived) children in the list. */
  activeChildren: number;
  /** Name chain from the root, e.g. "Aydınlatma › Panel". */
  trail: string;
};

const byOrder = (a: Category, b: Category) => Number(a.sortOrder) - Number(b.sortOrder) || a.name.localeCompare(b.name, "tr");

/**
 * The category list as a depth-first tree (siblings by sort order, then name). Rows whose parent is not in the list
 * (an archived parent while archived rows are hidden) are shown as roots, so nothing disappears.
 */
export function categoryTree(categories: readonly Category[]): CategoryNode[] {
  const ids = new Set(categories.map((c) => c.id));
  const children = new Map<string | null, Category[]>();
  for (const c of categories) {
    const parent = c.parentId && ids.has(c.parentId) ? c.parentId : null;
    const list = children.get(parent) ?? [];
    list.push(c);
    children.set(parent, list);
  }
  const out: CategoryNode[] = [];
  const seen = new Set<string>();
  const visit = (parent: string | null, level: number, trail: string) => {
    for (const c of (children.get(parent) ?? []).sort(byOrder)) {
      if (seen.has(c.id)) continue;
      seen.add(c.id);
      const path = trail ? `${trail} › ${c.name}` : c.name;
      const activeChildren = (children.get(c.id) ?? []).filter((x) => !x.isArchived).length;
      out.push({ category: c, level, activeChildren, trail: path });
      visit(c.id, level + 1, path);
    }
  };
  visit(null, 1, "");
  return out;
}

/**
 * True when a child may be added under this node (API: depth ≤ 4, active parent). The API's `depth` counts too: a row
 * shown as a root because its archived parent is hidden is still deep in the tree.
 */
export function canAddChild(node: Pick<CategoryNode, "level" | "category">): boolean {
  const depth = Math.max(node.level, Number(node.category.depth) || 0);
  return !node.category.isArchived && depth < CATEGORY_MAX_DEPTH;
}

/** Brands A→Z (Turkish collation), archived last. */
export function sortBrands(brands: readonly Brand[]): Brand[] {
  return [...brands].sort((a, b) => Number(a.isArchived) - Number(b.isArchived) || a.name.localeCompare(b.name, "tr"));
}

/** Units by code, archived last. */
export function sortUnits(units: readonly Unit[]): Unit[] {
  return [...units].sort((a, b) => Number(a.isArchived) - Number(b.isArchived) || a.code.localeCompare(b.code, "tr"));
}
