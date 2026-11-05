import { describe, expect, it } from "vitest";
import {
  addDays,
  defaultRange,
  formatRange,
  istanbulToday,
  isIsoDate,
  listDays,
  matchPreset,
  presetRange,
  rangeDays,
  rangeError,
  rangeFromParams,
} from "./date-range";

const params = (values: Record<string, string>) => new URLSearchParams(values);

describe("istanbulToday", () => {
  it("uses the fixed UTC+03:00 calendar day", () => {
    expect(istanbulToday(new Date("2026-11-05T20:59:59Z"))).toBe("2026-11-05");
    expect(istanbulToday(new Date("2026-11-05T21:00:00Z"))).toBe("2026-11-06");
  });
});

describe("presets", () => {
  const today = "2026-11-05";
  it("today, 7 days, 30 days and this month are inclusive ranges ending today", () => {
    expect(presetRange("today", today)).toEqual({ from: today, to: today });
    expect(presetRange("7d", today)).toEqual({ from: "2026-10-30", to: today });
    expect(presetRange("30d", today)).toEqual({ from: "2026-10-07", to: today });
    expect(presetRange("month", today)).toEqual({ from: "2026-11-01", to: today });
    expect(rangeDays(presetRange("7d", today))).toBe(7);
    expect(rangeDays(presetRange("30d", today))).toBe(30);
  });

  it("the default is the API's default (last 30 days)", () => {
    expect(defaultRange(today)).toEqual(presetRange("30d", today));
  });

  it("matches a preset, Bugün first on the 1st of the month; custom ranges match none", () => {
    expect(matchPreset({ from: "2026-10-07", to: today }, today)).toBe("30d");
    expect(matchPreset({ from: "2026-11-01", to: "2026-11-01" }, "2026-11-01")).toBe("today");
    expect(matchPreset({ from: "2026-10-01", to: "2026-10-31" }, today)).toBeNull();
  });

  it("crosses month and leap-year boundaries", () => {
    expect(addDays("2028-03-01", -1)).toBe("2028-02-29");
    expect(presetRange("7d", "2027-01-03")).toEqual({ from: "2026-12-28", to: "2027-01-03" });
  });
});

describe("rangeError", () => {
  it("accepts up to 366 days and rejects longer, inverted or malformed ranges", () => {
    expect(rangeError({ from: "2025-11-05", to: "2026-11-05" })).toBeNull(); // 366 days
    expect(rangeError({ from: "2025-11-04", to: "2026-11-05" })).toBe("Tarih aralığı en fazla 366 gün olabilir.");
    expect(rangeError({ from: "2026-11-06", to: "2026-11-05" })).toBe("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
    expect(rangeError({ from: "2026-02-30", to: "2026-03-01" })).toBe("Geçerli bir başlangıç ve bitiş tarihi seçin.");
    expect(rangeError({ from: "", to: "2026-03-01" })).not.toBeNull();
  });

  it("isIsoDate rejects impossible days", () => {
    expect(isIsoDate("2026-02-28")).toBe(true);
    expect(isIsoDate("2026-02-29")).toBe(false);
    expect(isIsoDate("26-02-01")).toBe(false);
    expect(isIsoDate(null)).toBe(false);
  });
});

describe("rangeFromParams", () => {
  const today = "2026-11-05";
  it("defaults to the last 30 days without parameters", () => {
    expect(rangeFromParams(params({}), today)).toEqual({ from: "2026-10-07", to: today });
  });
  it("reads baslangic/bitis and fills a missing or malformed side from the default", () => {
    expect(rangeFromParams(params({ baslangic: "2026-09-01", bitis: "2026-09-30" }), today)).toEqual({ from: "2026-09-01", to: "2026-09-30" });
    expect(rangeFromParams(params({ baslangic: "2026-11-01", bitis: "x" }), today)).toEqual({ from: "2026-11-01", to: today });
    expect(rangeFromParams(params({ baslangic: "nope" }), today)).toEqual(defaultRange(today));
  });
  it("keeps an inverted range so the page can explain it instead of silently fixing it", () => {
    const range = rangeFromParams(params({ baslangic: "2026-11-05", bitis: "2026-11-01" }), today);
    expect(rangeError(range)).not.toBeNull();
  });
});

describe("listDays / formatRange", () => {
  it("lists every day of a valid range and nothing for an invalid one", () => {
    expect(listDays({ from: "2026-10-30", to: "2026-11-02" })).toEqual(["2026-10-30", "2026-10-31", "2026-11-01", "2026-11-02"]);
    expect(listDays({ from: "2026-11-02", to: "2026-11-01" })).toEqual([]);
  });
  it("formats without a time-zone shift", () => {
    expect(formatRange({ from: "2026-11-05", to: "2026-11-05" })).toBe("5 Kas 2026");
    expect(formatRange({ from: "2026-10-07", to: "2026-11-05" })).toBe("7 Eki 2026 – 5 Kas 2026");
  });
});
