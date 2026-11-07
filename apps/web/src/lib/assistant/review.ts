import { parseQuantity, quantityText } from "@/lib/inventory/quantity";
import type { ApproveOrderParseRequest, OrderParse, OrderParseCandidate, OrderParseLine } from "./api";

/**
 * Review state of a WhatsApp order parse (ADR 0010 §2.4). Rules the screen must never break:
 * - the AI only proposes: a matched line starts with its proposal filled in but NOT confirmed — a person confirms it;
 * - an ambiguous or unmatched line starts with no variant at all (never a default candidate);
 * - every kept line needs an explicit, confirmed variant and a valid quantity before the draft can be created;
 * - nothing here carries a price or a stock level (prices come from Pricing once the draft exists).
 */

/** Where the chosen variant came from (mirrors the API's `selection`: proposed, candidate, manual). */
export type VariantSource = "proposed" | "candidate" | "manual";

export type ChosenVariant = {
  id: string;
  sku: string;
  name: string | null;
  /** Catalog unit of the variant (null when picked from a search that did not return it). */
  unit: string | null;
  source: VariantSource;
};

export type ReviewLine = {
  lineNo: number;
  matchStatus: string;
  /** false = the line is left out of the draft (sent as include = false). */
  include: boolean;
  variant: ChosenVariant | null;
  /** A person confirmed the variant: picked a candidate / a search result, or ticked "confirm" on the proposal. */
  confirmed: boolean;
  /** Quantity as typed (Turkish decimal comma allowed). */
  quantityText: string;
};

export type ReviewState = { lines: ReviewLine[] };

export type ReviewAction =
  | { type: "reset"; parse: OrderParse }
  | { type: "include"; lineNo: number; include: boolean }
  | { type: "confirmProposal"; lineNo: number; confirmed: boolean }
  | { type: "choose"; lineNo: number; variant: Omit<ChosenVariant, "source">; source: Exclude<VariantSource, "proposed"> }
  | { type: "clearVariant"; lineNo: number }
  | { type: "quantity"; lineNo: number; text: string };

/** The approve endpoint's upper bound (ParsedOrderValidator.MaxQuantity). */
export const MAX_REVIEW_QUANTITY = 1_000_000;

const num = (value: number | string | null | undefined): number | null => {
  if (value === null || value === undefined || value === "") return null;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : null;
};

/** The proposal of a matched line, taken from its candidates (the API lists the proposal among them). */
export function proposalOf(line: OrderParseLine): OrderParseCandidate | null {
  if (line.matchStatus !== "matched" || !line.proposedVariantId) return null;
  return line.candidates.find((c) => c.variantId === line.proposedVariantId) ?? null;
}

export function initialLine(line: OrderParseLine): ReviewLine {
  const proposal = proposalOf(line);
  const quantity = num(line.quantity);
  const lineNo = num(line.lineNo) ?? 0;
  return {
    lineNo,
    matchStatus: line.matchStatus,
    include: true,
    // Only a matched line gets a pre-filled variant, and only its own proposal; it still has to be confirmed.
    variant: proposal ? { id: proposal.variantId, sku: proposal.sku, name: proposal.name, unit: proposal.unit, source: "proposed" } : null,
    confirmed: false,
    quantityText: quantity !== null && quantity > 0 ? quantityText(quantity) : "",
  };
}

export function initialReview(parse: OrderParse): ReviewState {
  return { lines: parse.lines.map(initialLine) };
}

export function reviewReducer(state: ReviewState, action: ReviewAction): ReviewState {
  if (action.type === "reset") return initialReview(action.parse);
  const update = (patch: (line: ReviewLine) => ReviewLine) => ({
    lines: state.lines.map((line) => (line.lineNo === action.lineNo ? patch(line) : line)),
  });
  switch (action.type) {
    case "include":
      return update((line) => ({ ...line, include: action.include }));
    case "confirmProposal":
      // Only a proposal can be confirmed with the checkbox; other choices are confirmed by choosing them.
      return update((line) => (line.variant?.source === "proposed" ? { ...line, confirmed: action.confirmed } : line));
    case "choose":
      return update((line) => ({ ...line, variant: { ...action.variant, source: action.source }, confirmed: true }));
    case "clearVariant":
      return update((line) => ({ ...line, variant: null, confirmed: false }));
    case "quantity":
      return update((line) => ({ ...line, quantityText: action.text }));
  }
}

export type QuantityCheck = { ok: true; value: number } | { ok: false; message: string };

/** > 0, at most 3 decimals, at most 1 000 000 (the approve endpoint's rules). */
export function checkQuantity(text: string): QuantityCheck {
  const parsed = parseQuantity(text);
  if (!parsed.ok) return parsed;
  if (parsed.value > MAX_REVIEW_QUANTITY) return { ok: false, message: "Miktar en fazla 1.000.000 olabilir." };
  return parsed;
}

export type LineIssue = { kind: "variant" | "confirm" | "quantity"; message: string };

/** What still blocks a kept line (null when it is ready; left-out lines never block). */
export function lineIssues(line: ReviewLine): LineIssue[] {
  if (!line.include) return [];
  const issues: LineIssue[] = [];
  if (!line.variant) {
    issues.push({
      kind: "variant",
      message:
        line.matchStatus === "ambiguous"
          ? "Adaylardan birini seçin veya satırı çıkarın."
          : "Ürünü katalogda arayıp seçin veya satırı çıkarın.",
    });
  } else if (!line.confirmed) {
    issues.push({ kind: "confirm", message: "Önerilen ürünü onaylayın veya başka bir ürün seçin." });
  }
  const quantity = checkQuantity(line.quantityText);
  if (!quantity.ok) issues.push({ kind: "quantity", message: quantity.message });
  return issues;
}

export type ReviewSummary = {
  kept: number;
  skipped: number;
  /** Kept lines that still need a choice, a confirmation or a quantity. */
  pending: number;
  canConfirm: boolean;
  /** Why the confirm button is disabled (null when enabled). */
  blocker: string | null;
};

export function reviewSummary(state: ReviewState): ReviewSummary {
  const kept = state.lines.filter((l) => l.include);
  const pending = kept.filter((l) => lineIssues(l).length > 0).length;
  const blocker =
    state.lines.length === 0
      ? "Mesajda sipariş satırı bulunamadı."
      : kept.length === 0
        ? "Tüm satırlar çıkarıldı. En az bir satırı tutun veya ayrıştırmayı reddedin."
        : pending > 0
          ? `${pending} satır seçim, onay veya miktar bekliyor.`
          : null;
  return { kept: kept.length, skipped: state.lines.length - kept.length, pending, canConfirm: blocker === null, blocker };
}

/**
 * The approve body: every review line exactly once; kept lines name their variant and quantity explicitly (also for a
 * confirmed proposal), left-out lines are sent with include = false. Throws when the state is not confirmable — the
 * button is disabled then, so this is a programming error.
 */
export function buildApproveRequest(
  state: ReviewState,
  header: { customerId: string | null; warehouseId: string | null; notes: string },
): ApproveOrderParseRequest {
  if (!reviewSummary(state).canConfirm) throw new Error("The review is not complete.");
  return {
    customerId: header.customerId,
    warehouseId: header.warehouseId || null,
    notes: header.notes.trim() ? header.notes.trim() : null,
    lines: state.lines.map((line) => {
      if (!line.include) return { lineNo: line.lineNo, include: false, variantId: null, quantity: null };
      const quantity = checkQuantity(line.quantityText);
      // The unit the reviewer saw next to the quantity: the API rejects it (422) when it is no longer the variant's
      // unit, so a quantity is never ordered in a unit nobody looked at. Null when the search did not return a unit.
      return {
        lineNo: line.lineNo,
        include: true,
        variantId: line.variant!.id,
        quantity: quantity.ok ? quantity.value : null,
        unit: line.variant!.unit,
      };
    }),
  };
}

/**
 * Maps the approve 422 keys (`lines[i].variantId`, `lines[i].quantity` — `i` = index in the request, which lists the
 * lines in review order) back to review line numbers.
 */
export function lineErrorsFromFields(state: ReviewState, fieldErrors: Record<string, string>): Map<number, string[]> {
  const result = new Map<number, string[]>();
  for (const [key, message] of Object.entries(fieldErrors)) {
    const match = /^lines\[(\d+)\]/.exec(key);
    if (!match) continue;
    const line = state.lines[Number(match[1])];
    if (!line) continue;
    result.set(line.lineNo, [...(result.get(line.lineNo) ?? []), message]);
  }
  return result;
}

export type MessageSegment = { text: string; lineNo: number | null };

export type SegmentLine = {
  lineNo: number | string;
  rawText: string;
  /** API offsets into messageText (UTF-16 code units); null/absent when the server could not locate the line. */
  textStart?: number | string | null;
  textLength?: number | string | null;
};

/** The API's span of a line when it is usable for this message (in range, non-empty). */
function offsetSpan(message: string, line: SegmentLine): { start: number; end: number } | null {
  const start = num(line.textStart);
  const length = num(line.textLength);
  if (start === null || length === null || !Number.isInteger(start) || !Number.isInteger(length)) return null;
  if (start < 0 || length <= 0 || start + length > message.length) return null;
  return { start, end: start + length };
}

/**
 * Splits the message into plain-text segments, marking the part each line was read from. The API's character offsets
 * (`textStart`/`textLength`) are used when present; otherwise the span is found by `rawText`, in order. A line that
 * cannot be located, or would overlap another, is not marked. The result is rendered as React text only — never as HTML.
 */
export function messageSegments(message: string, lines: readonly SegmentLine[]): MessageSegment[] {
  const spans: { start: number; end: number; lineNo: number }[] = [];
  let cursor = 0;
  for (const line of lines) {
    const raw = line.rawText.trim();
    const offsets = offsetSpan(message, line);
    let start: number;
    let end: number;
    if (offsets) {
      ({ start, end } = offsets);
    } else {
      if (!raw) continue;
      start = message.indexOf(raw, cursor);
      if (start < 0) start = message.indexOf(raw);
      if (start < 0) continue;
      end = start + raw.length;
    }
    if (spans.some((s) => start < s.end && end > s.start)) continue;
    spans.push({ start, end, lineNo: Number(line.lineNo) });
    cursor = end;
  }
  spans.sort((a, b) => a.start - b.start);
  const segments: MessageSegment[] = [];
  let pos = 0;
  for (const span of spans) {
    if (span.start > pos) segments.push({ text: message.slice(pos, span.start), lineNo: null });
    segments.push({ text: message.slice(span.start, span.end), lineNo: span.lineNo });
    pos = span.end;
  }
  if (pos < message.length) segments.push({ text: message.slice(pos), lineNo: null });
  return segments;
}
