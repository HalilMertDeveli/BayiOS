import { describe, expect, it } from "vitest";
import { addQuantities, formatQuantity, formatSignedQuantity, parseQuantity, quantityText, toNumber } from "./quantity";

describe("parseQuantity", () => {
  it("accepts integers, Turkish commas and dots", () => {
    expect(parseQuantity("12")).toEqual({ ok: true, value: 12 });
    expect(parseQuantity(" 12,5 ")).toEqual({ ok: true, value: 12.5 });
    expect(parseQuantity("0.125")).toEqual({ ok: true, value: 0.125 });
  });

  it("rejects empty, zero, negative, thousands separators and > 3 decimals", () => {
    expect(parseQuantity("").ok).toBe(false);
    expect(parseQuantity("0")).toMatchObject({ ok: false, message: "Miktar 0'dan büyük olmalı." });
    expect(parseQuantity("-1").ok).toBe(false);
    expect(parseQuantity("1.250,5").ok).toBe(false);
    expect(parseQuantity("1,2345")).toMatchObject({ ok: false, message: "En fazla 3 ondalık basamak girin." });
    expect(parseQuantity("abc").ok).toBe(false);
    expect(parseQuantity("1e3").ok).toBe(false);
  });

  it("can require whole numbers", () => {
    expect(parseQuantity("2,5", { allowFraction: false }).ok).toBe(false);
    expect(parseQuantity("2", { allowFraction: false })).toEqual({ ok: true, value: 2 });
  });
});

describe("formatting", () => {
  it("formats numbers and numeric strings the Turkish way", () => {
    expect(formatQuantity(1250.5)).toBe("1.250,5");
    expect(formatQuantity("3")).toBe("3");
    expect(formatQuantity(null)).toBe("—");
    expect(formatSignedQuantity(5)).toBe("+5");
    expect(formatSignedQuantity("-2.5")).toBe("−2,5");
    expect(formatSignedQuantity(0)).toBe("0");
  });

  it("adds without floating-point noise and round-trips to input text", () => {
    expect(addQuantities(0.1, 0.2)).toBe(0.3);
    expect(quantityText(12.5)).toBe("12,5");
    expect(toNumber("7")).toBe(7);
    expect(toNumber(undefined)).toBe(0);
  });
});
