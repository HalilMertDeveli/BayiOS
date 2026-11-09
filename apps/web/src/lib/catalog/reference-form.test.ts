import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import type { Brand, Category, Unit } from "./api";
import { CATALOG_CONFLICTS, serverFieldErrors } from "./errors";
import {
  buildCategoryCreate,
  buildCategoryPatch,
  buildUnitCreate,
  buildUnitPatch,
  canAddChild,
  categoryTree,
  cleanName,
  parseSortOrder,
  sortBrands,
  validateBrandName,
  validateCategory,
  validateUnit,
} from "./reference-form";

const NOW = "2026-11-01T00:00:00Z";
const cat = (id: string, parentId: string | null, name: string, extra: Partial<Category> = {}): Category => ({
  id,
  parentId,
  name,
  path: `/${id}/`,
  depth: 1,
  sortOrder: 0,
  isArchived: false,
  version: 1,
  createdAt: NOW,
  updatedAt: null,
  ...extra,
});
const unit = (extra: Partial<Unit> = {}): Unit => ({ id: "u1", code: "kg", name: "Kilogram", allowsFraction: true, isArchived: false, version: 4, createdAt: NOW, updatedAt: null, ...extra });
const brand = (name: string, isArchived = false): Brand => ({ id: name, name, isArchived, version: 1, createdAt: NOW, updatedAt: null });

describe("reference form validation (same limits as the API)", () => {
  it("names are trimmed with inner whitespace collapsed; brand 1–100", () => {
    expect(cleanName("  Simya   Motor ")).toBe("Simya Motor");
    expect(validateBrandName("   ")).toEqual({ name: "Bu alan zorunlu." });
    expect(validateBrandName("x".repeat(101))).toEqual({ name: "En fazla 100 karakter girin." });
    expect(validateBrandName("Simya")).toEqual({});
  });

  it("unit code: required, no whitespace, ≤ 20, only on create; name ≤ 50", () => {
    expect(validateUnit({ code: "", name: "Adet", allowsFraction: false }, "create")).toEqual({ code: "Bu alan zorunlu." });
    expect(validateUnit({ code: "a b", name: "Adet", allowsFraction: false }, "create").code).toMatch(/Boşluk içermeyen/);
    expect(validateUnit({ code: "x".repeat(21), name: "Adet", allowsFraction: false }, "create").code).toMatch(/1–20/);
    expect(validateUnit({ code: "", name: "x".repeat(51), allowsFraction: false }, "edit")).toEqual({ name: "En fazla 50 karakter girin." });
    expect(buildUnitCreate({ code: " KG ", name: " Kilo  gram ", allowsFraction: true })).toEqual({ code: "KG", name: "Kilo gram", allowsFraction: true });
  });

  it("unit patch carries changed members only", () => {
    expect(buildUnitPatch({ code: "kg", name: "Kilogram", allowsFraction: true }, unit())).toEqual({});
    expect(buildUnitPatch({ code: "kg", name: "Kilo", allowsFraction: false }, unit())).toEqual({ name: "Kilo", allowsFraction: false });
  });

  it("category sort order: empty = 0, integers only", () => {
    expect(parseSortOrder("")).toBe(0);
    expect(parseSortOrder(" -3 ")).toBe(-3);
    expect(parseSortOrder("1,5")).toBeNull();
    expect(validateCategory({ name: "Panel", sortOrder: "x" })).toEqual({ sortOrder: "Tam sayı girin (boş = 0)." });
    expect(buildCategoryCreate({ name: " Panel ", sortOrder: "" }, "p1")).toEqual({ name: "Panel", parentId: "p1", sortOrder: 0 });
    expect(buildCategoryPatch({ name: "Panel", sortOrder: "2" }, cat("c", null, "Panel"))).toEqual({ sortOrder: 2 });
  });
});

describe("categoryTree", () => {
  const list = [
    cat("motor", null, "Motor", { sortOrder: 1 }),
    cat("led", null, "Aydınlatma", { sortOrder: 0 }),
    cat("panel", "led", "Panel", { depth: 2 }),
    cat("ray", "led", "Ray spot", { depth: 2, isArchived: true }),
    cat("60x60", "panel", "60x60", { depth: 3 }),
    cat("orphan", "gone", "Yetim", { depth: 3 }),
  ];

  it("depth-first by sort order then name; levels from the parent links; trail and active children", () => {
    const nodes = categoryTree(list);
    expect(nodes.map((n) => [n.category.id, n.level])).toEqual([
      ["led", 1],
      ["panel", 2],
      ["60x60", 3],
      ["ray", 2],
      // The orphan (parent not in the list) is shown as a root, ordered like the other roots.
      ["orphan", 1],
      ["motor", 1],
    ]);
    expect(nodes.find((n) => n.category.id === "60x60")?.trail).toBe("Aydınlatma › Panel › 60x60");
    // The archived "Ray spot" does not block archiving "Aydınlatma".
    expect(nodes.find((n) => n.category.id === "led")?.activeChildren).toBe(1);
  });

  it("children only up to depth 4, never under an archived category; an orphan keeps its real depth", () => {
    const nodes = categoryTree([...list, cat("d4", "60x60", "Dört", { depth: 4 })]);
    const of = (id: string) => nodes.find((n) => n.category.id === id)!;
    expect(canAddChild(of("60x60"))).toBe(true);
    expect(canAddChild(of("d4"))).toBe(false);
    expect(canAddChild(of("ray"))).toBe(false);
    expect(of("orphan").level).toBe(1);
    expect(canAddChild({ ...of("orphan"), category: { ...of("orphan").category, depth: 4 } })).toBe(false);
  });

  it("brands sort A→Z in Turkish, archived last", () => {
    expect(sortBrands([brand("Zeta"), brand("Çelik"), brand("Ayna", true), brand("Cam")]).map((b) => b.name)).toEqual(["Cam", "Çelik", "Zeta", "Ayna"]);
  });
});

describe("reference API problems → Turkish", () => {
  it("409 conflict codes have texts", () => {
    for (const code of ["catalog.brand_name_taken", "catalog.unit_code_taken", "catalog.category_name_taken", "catalog.category_has_children"]) {
      expect(CATALOG_CONFLICTS[code]).toBeTruthy();
    }
  });

  it("the API's English validation messages are translated", () => {
    const error = new ApiError("x", 422, {
      code: "validation.failed",
      errors: {
        code: ["Code must be 1–20 characters without whitespace."],
        parentId: ["Category tree depth is limited to 4."],
        other: ["Parent category not found."],
      },
    });
    expect(serverFieldErrors(error)).toEqual({
      code: "Boşluk içermeyen 1–20 karakter girin.",
      parentId: "Kategori ağacı en fazla 4 seviye olabilir; bu kategorinin altına kategori eklenemez.",
      other: "Üst kategori bulunamadı veya arşivlenmiş. Sayfayı yenileyin.",
    });
  });
});
