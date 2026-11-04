import { describe, expect, it } from "vitest";
import { categoryOptions, normalizeNumericFilter, parseListQuery, toApiQuery, toSearchString } from "./list-query";
import type { Category } from "./api";

const cat = (id: string, name: string, parentId: string | null = null, sortOrder = 0): Category => ({
  id,
  name,
  parentId,
  path: id,
  depth: parentId ? 1 : 0,
  sortOrder,
  isArchived: false,
  version: 1,
  createdAt: "2026-11-01T00:00:00Z",
  updatedAt: null,
});

describe("URL ⇄ list query", () => {
  it("parses search, filters and attr.* filters; drops unknown values", () => {
    const query = parseListQuery(new URLSearchParams("q=fren&categoryId=c1&status=bogus&sort=name&attr.guc.min=10&attr.renk=Sar%C4%B1&x=1"));
    expect(query).toEqual({
      q: "fren",
      categoryId: "c1",
      brandId: "",
      status: "",
      sort: "name",
      attributes: { "attr.guc.min": "10", "attr.renk": "Sarı" },
    });
  });

  it("round-trips through the page URL", () => {
    const search = "attr.guc.min=10&brandId=b1&q=fren&status=archived";
    expect(toSearchString(parseListQuery(new URLSearchParams(search)))).toBe("q=fren&brandId=b1&status=archived&attr.guc.min=10");
  });

  it("builds the API query with limit, cursor and the expanded 'all' status", () => {
    const query = parseListQuery(new URLSearchParams("q=a&status=all&attr.renk=Mavi"));
    const api = new URLSearchParams(toApiQuery(query, "CURSOR"));
    expect(api.get("status")).toBe("active,inactive,archived");
    expect(api.get("limit")).toBe("50");
    expect(api.get("cursor")).toBe("CURSOR");
    expect(api.get("attr.renk")).toBe("Mavi");
    expect(api.get("q")).toBe("a");
  });

  it("caps attr filters at the API limit", () => {
    const params = new URLSearchParams(Array.from({ length: 12 }, (_, i) => `attr.f${i}=1`).join("&"));
    expect(Object.keys(parseListQuery(params).attributes)).toHaveLength(10);
  });

  it("normalizes Turkish decimals for numeric filters", () => {
    expect(normalizeNumericFilter("12,5")).toBe("12.5");
    expect(normalizeNumericFilter("12")).toBe("12");
  });
});

describe("categoryOptions", () => {
  it("lists categories in tree order with indentation (subcategories under their parent)", () => {
    const options = categoryOptions([cat("c2", "Fren", "c1"), cat("c1", "Motor"), cat("c3", "Aydınlatma", null, -1)]);
    expect(options.map((o) => o.value)).toEqual(["c3", "c1", "c2"]);
    expect(options[2].label).toContain("— Fren");
  });
});
