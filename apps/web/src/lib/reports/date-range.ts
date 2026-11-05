/**
 * Report date ranges (api-strategy §12.13): inclusive Europe/Istanbul calendar days as `yyyy-MM-dd`, default the last 30
 * days including today, at most 366 days. Turkey has a fixed UTC+03:00 offset (no DST since 2016) — the API uses the same
 * rule, so "today" here is the API's today. Dates are handled as text / UTC-midnight arithmetic, never local time.
 */

export const MAX_RANGE_DAYS = 366;
export const DEFAULT_RANGE_DAYS = 30;

const ISTANBUL_OFFSET_MS = 3 * 60 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

export type DateRange = { from: string; to: string };

/** Today's Istanbul calendar day. */
export function istanbulToday(now: Date = new Date()): string {
  return new Date(now.getTime() + ISTANBUL_OFFSET_MS).toISOString().slice(0, 10);
}

/** A real calendar day in yyyy-MM-dd (rejects 2026-02-30). */
export function isIsoDate(value: string | null | undefined): value is string {
  if (!value || !DATE.test(value)) return false;
  const time = Date.parse(`${value}T00:00:00Z`);
  return !Number.isNaN(time) && new Date(time).toISOString().slice(0, 10) === value;
}

export function addDays(day: string, days: number): string {
  return new Date(Date.parse(`${day}T00:00:00Z`) + days * DAY_MS).toISOString().slice(0, 10);
}

/** Inclusive number of days in the range (1 for a single day). */
export function rangeDays(range: DateRange): number {
  return Math.round((Date.parse(`${range.to}T00:00:00Z`) - Date.parse(`${range.from}T00:00:00Z`)) / DAY_MS) + 1;
}

export type PresetId = "today" | "7d" | "30d" | "month";

export const PRESETS: readonly { id: PresetId; label: string }[] = [
  { id: "today", label: "Bugün" },
  { id: "7d", label: "Son 7 gün" },
  { id: "30d", label: "Son 30 gün" },
  { id: "month", label: "Bu ay" },
];

export function presetRange(id: PresetId, today: string): DateRange {
  switch (id) {
    case "today":
      return { from: today, to: today };
    case "7d":
      return { from: addDays(today, -6), to: today };
    case "30d":
      return { from: addDays(today, -(DEFAULT_RANGE_DAYS - 1)), to: today };
    case "month":
      return { from: `${today.slice(0, 8)}01`, to: today };
  }
}

/** The preset a range equals (first match wins: on the 1st of a month "Bugün" beats "Bu ay"), or null for a custom range. */
export function matchPreset(range: DateRange, today: string): PresetId | null {
  for (const { id } of PRESETS) {
    const preset = presetRange(id, today);
    if (preset.from === range.from && preset.to === range.to) return id;
  }
  return null;
}

export const defaultRange = (today: string): DateRange => presetRange("30d", today);

/** Error text for a range the API would reject (422), or null. */
export function rangeError(range: { from: string; to: string }): string | null {
  if (!isIsoDate(range.from) || !isIsoDate(range.to)) return "Geçerli bir başlangıç ve bitiş tarihi seçin.";
  if (range.from > range.to) return "Başlangıç tarihi bitiş tarihinden sonra olamaz.";
  if (rangeDays(range) > MAX_RANGE_DAYS) return `Tarih aralığı en fazla ${MAX_RANGE_DAYS} gün olabilir.`;
  return null;
}

/**
 * Range from the URL (`baslangic`, `bitis`): missing or malformed values fall back to the default (last 30 days). The
 * result may still be invalid (from > to, too long) — callers show `rangeError` instead of requesting.
 */
export function rangeFromParams(params: { get(name: string): string | null }, today: string): DateRange {
  const from = params.get("baslangic");
  const to = params.get("bitis");
  if (!isIsoDate(from) && !isIsoDate(to)) return defaultRange(today);
  const fallback = defaultRange(today);
  return { from: isIsoDate(from) ? from : fallback.from, to: isIsoDate(to) ? to : fallback.to };
}

/** Every day of the range in order (to fill gaps in a daily chart with zeros). Capped at MAX_RANGE_DAYS. */
export function listDays(range: DateRange): string[] {
  if (rangeError(range)) return [];
  const days: string[] = [];
  for (let day = range.from; day <= range.to && days.length < MAX_RANGE_DAYS; day = addDays(day, 1)) days.push(day);
  return days;
}

const dayFormat = new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
const shortDayFormat = new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "short", timeZone: "UTC" });

/** "5 Kas 2026" for a yyyy-MM-dd calendar day (no time-zone shift). */
export function formatDay(day: string): string {
  return isIsoDate(day) ? dayFormat.format(new Date(`${day}T00:00:00Z`)) : day;
}

export function formatShortDay(day: string): string {
  return isIsoDate(day) ? shortDayFormat.format(new Date(`${day}T00:00:00Z`)) : day;
}

/** "7 Eki 2026 – 5 Kas 2026" (one day: just the day). */
export function formatRange(range: DateRange): string {
  return range.from === range.to ? formatDay(range.from) : `${formatDay(range.from)} – ${formatDay(range.to)}`;
}
