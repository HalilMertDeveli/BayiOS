import { describe, expect, it } from "vitest";
import { currencyOptions, formatMoney, formatPercent, formatQuantity, isZeroDecimal, parseDecimalInput, sameDecimal, toDecimalInput } from "./money";

const nbsp = (s: string | null) => s?.replace(/ /g, " ") ?? null;

describe("formatMoney (Intl tr-TR)", () => {
  it("formats numbers and numeric strings in the list currency", () => {
    expect(formatMoney(1234.5, "TRY")).toBe("₺1.234,50");
    expect(formatMoney("1234.5", "TRY")).toBe("₺1.234,50");
    expect(nbsp(formatMoney("99.9", "EUR"))).toBe("€99,90");
    expect(nbsp(formatMoney(12, "USD"))).toBe("$12,00");
  });

  it("keeps up to 4 decimals exactly, without a float round trip", () => {
    expect(formatMoney("0.1235", "TRY")).toBe("₺0,1235");
    // 2^53 + 1 is not representable as a double; the string path keeps the last digit.
    expect(formatMoney("9007199254740993.5", "TRY")).toBe("₺9.007.199.254.740.993,50");
  });

  it("returns null for a missing amount, so the UI can say 'fiyat yok' instead of 0", () => {
    expect(formatMoney(null, "TRY")).toBeNull();
    expect(formatMoney(undefined, "TRY")).toBeNull();
    expect(formatMoney("", "TRY")).toBeNull();
  });

  it("still formats 0 as a real zero price (0 ≠ no price)", () => {
    expect(formatMoney(0, "TRY")).toBe("₺0,00");
  });

  it("falls back to number + code for an unknown currency", () => {
    expect(formatMoney(5, "??")).toBe("5 ??");
  });
});

describe("percent and quantity", () => {
  it("formats percents Turkish style", () => {
    expect(formatPercent(12.5)).toBe("%12,5");
    expect(formatPercent("7.25")).toBe("%7,25");
    expect(formatPercent(null)).toBeNull();
  });

  it("formats quantities with at most 3 decimals", () => {
    expect(formatQuantity(1000)).toBe("1.000");
    expect(formatQuantity("2.5")).toBe("2,5");
  });
});

describe("parseDecimalInput", () => {
  it("accepts a comma or a dot and normalises to the API's decimal string", () => {
    expect(parseDecimalInput("12,50", 4)).toEqual({ ok: true, value: "12.50" });
    expect(parseDecimalInput(" 12.5 ", 4)).toEqual({ ok: true, value: "12.5" });
    expect(parseDecimalInput("007", 4)).toEqual({ ok: true, value: "7" });
    expect(parseDecimalInput("0,0001", 4)).toEqual({ ok: true, value: "0.0001" });
  });

  it("rejects signs, thousands separators, letters and too many decimals", () => {
    for (const text of ["-1", "1.234,50", "abc", "1,23456", "", "1e3"]) expect(parseDecimalInput(text, 4)).toEqual({ ok: false });
    expect(parseDecimalInput("1,2345", 3)).toEqual({ ok: false });
  });

  it("round-trips API values through the input format", () => {
    expect(toDecimalInput(12.5)).toBe("12,5");
    expect(toDecimalInput("100.0000")).toBe("100,0000");
    expect(toDecimalInput(null)).toBe("");
  });

  it("compares decimals by value as text", () => {
    expect(sameDecimal("12.50", 12.5)).toBe(true);
    expect(sameDecimal("012", "12.000")).toBe(true);
    expect(sameDecimal(null, "")).toBe(true);
    expect(sameDecimal("1", null)).toBe(false);
    expect(isZeroDecimal("0.000")).toBe(true);
    expect(isZeroDecimal("0.01")).toBe(false);
  });
});

describe("currencyOptions", () => {
  it("keeps a stored currency that is not in the default set", () => {
    expect(currencyOptions("CHF").map((o) => o.value)).toEqual(["TRY", "USD", "EUR", "GBP", "CHF"]);
    expect(currencyOptions("TRY")).toHaveLength(4);
  });
});
