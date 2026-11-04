import { describe, expect, it } from "vitest";
import { buildAttributePatch, buildAttributes, fieldsFor, formatAttribute, parseDecimal, toApiValue, toFormValue } from "./attributes";
import { field } from "./test-fixtures";

describe("parseDecimal", () => {
  it("accepts comma and dot decimals", () => {
    expect(parseDecimal("12,5")).toBe(12.5);
    expect(parseDecimal("12.5")).toBe(12.5);
    expect(parseDecimal("-3")).toBe(-3);
  });
  it("rejects thousands separators and text", () => {
    expect(parseDecimal("1.234,5")).toBeNull();
    expect(parseDecimal("abc")).toBeNull();
    expect(parseDecimal("")).toBeNull();
  });
});

describe("toApiValue — each data type", () => {
  it("text: trims, empty is absent, required empty is an error", () => {
    expect(toApiValue(field({ key: "t", dataType: "text" }), "  x  ")).toEqual({ kind: "value", value: "x" });
    expect(toApiValue(field({ key: "t", dataType: "text" }), "   ")).toEqual({ kind: "empty" });
    expect(toApiValue(field({ key: "t", dataType: "text", required: true }), "")).toEqual({ kind: "error", message: "Bu alan zorunlu." });
    expect(toApiValue(field({ key: "t", dataType: "text" }), "a".repeat(501)).kind).toBe("error");
  });

  it("number (with unit) parses Turkish decimals; integer rejects fractions", () => {
    const watt = field({ key: "guc", dataType: "number", unit: "W" });
    expect(toApiValue(watt, "12,5")).toEqual({ kind: "value", value: 12.5 });
    expect(toApiValue(watt, "on iki")).toMatchObject({ kind: "error" });
    const count = field({ key: "adet", dataType: "integer" });
    expect(toApiValue(count, "4")).toEqual({ kind: "value", value: 4 });
    expect(toApiValue(count, "4,5")).toEqual({ kind: "error", message: "Tam sayı girin (ör. 4)." });
  });

  it("boolean: '' is unset, 'false' is a real value", () => {
    const f = field({ key: "b", dataType: "boolean" });
    expect(toApiValue(f, "false")).toEqual({ kind: "value", value: false });
    expect(toApiValue(f, "true")).toEqual({ kind: "value", value: true });
    expect(toApiValue(f, "")).toEqual({ kind: "empty" });
  });

  it("enum and multi_enum only accept defined options; multi_enum keeps option order", () => {
    const color = field({ key: "renk", dataType: "enum", options: ["Kırmızı", "Mavi"] });
    expect(toApiValue(color, "Mavi")).toEqual({ kind: "value", value: "Mavi" });
    expect(toApiValue(color, "Yeşil").kind).toBe("error");
    const models = field({ key: "model", dataType: "multi_enum", options: ["A", "B", "C"] });
    expect(toApiValue(models, ["C", "A", "A"])).toEqual({ kind: "value", value: ["A", "C"] });
    expect(toApiValue(models, [])).toEqual({ kind: "empty" });
    expect(toApiValue({ ...models, required: true }, []).kind).toBe("error");
  });

  it("range: needs both bounds and min ≤ max", () => {
    const r = field({ key: "k", dataType: "range", unit: "K" });
    expect(toApiValue(r, { min: "2700", max: "6500" })).toEqual({ kind: "value", value: { min: 2700, max: 6500 } });
    expect(toApiValue(r, { min: "", max: "" })).toEqual({ kind: "empty" });
    expect(toApiValue(r, { min: "5", max: "" }).kind).toBe("error");
    expect(toApiValue(r, { min: "9", max: "1" })).toEqual({ kind: "error", message: "En az değer, en çok değerden büyük olamaz." });
  });

  it("date: yyyy-MM-dd only", () => {
    const d = field({ key: "d", dataType: "date" });
    expect(toApiValue(d, "2026-11-05")).toEqual({ kind: "value", value: "2026-11-05" });
    expect(toApiValue(d, "05.11.2026").kind).toBe("error");
  });
});

describe("toFormValue", () => {
  it("round-trips stored values into input strings", () => {
    expect(toFormValue(field({ key: "n", dataType: "number" }), 12.5)).toBe("12,5");
    expect(toFormValue(field({ key: "b", dataType: "boolean" }), false)).toBe("false");
    expect(toFormValue(field({ key: "r", dataType: "range" }), { min: 1, max: 2.5 })).toEqual({ min: "1", max: "2,5" });
    expect(toFormValue(field({ key: "m", dataType: "multi_enum" }), ["A"])).toEqual(["A"]);
    expect(toFormValue(field({ key: "m", dataType: "multi_enum" }), undefined)).toEqual([]);
  });
});

describe("buildAttributes / buildAttributePatch", () => {
  const fields = [
    field({ key: "guc", dataType: "number", required: true, unit: "W" }),
    field({ key: "renk", dataType: "enum", options: ["Sarı", "Beyaz"] }),
  ];

  it("keys errors like the API (prefix.key)", () => {
    const built = buildAttributes(fields, { guc: "", renk: "Sarı" }, "variants[1].attributes");
    expect(built.attributes).toEqual({ renk: "Sarı" });
    expect(built.errors).toEqual({ "variants[1].attributes.guc": "Bu alan zorunlu." });
  });

  it("patch sends only changes and null for cleared values; archived fields untouched", () => {
    const original = { guc: 10, renk: "Sarı", eski: "korunur" };
    const { patch, errors } = buildAttributePatch(fields, { guc: "10", renk: "" }, original, "attributes");
    expect(errors).toEqual({});
    expect(patch).toEqual({ renk: null });
  });
});

describe("fieldsFor / formatAttribute", () => {
  it("returns active fields of one record type in sort order", () => {
    const list = [
      field({ key: "b", dataType: "text", sortOrder: 2 }),
      field({ key: "a", dataType: "text", sortOrder: 1 }),
      field({ key: "v", dataType: "text", entityType: "variant" }),
      field({ key: "x", dataType: "text", isArchived: true }),
    ];
    expect(fieldsFor(list, "product").map((f) => f.key)).toEqual(["a", "b"]);
  });
  it("formats values for display", () => {
    expect(formatAttribute(field({ key: "g", dataType: "number", unit: "W" }), 7.5)).toBe("7,5 W");
    expect(formatAttribute(field({ key: "b", dataType: "boolean" }), true)).toBe("Evet");
  });
});
