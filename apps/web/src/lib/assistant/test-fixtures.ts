/**
 * MOCK WhatsApp order-parse payloads — synthetic test data only (Vitest). Shapes follow OrderParseResponse in
 * packages/contracts/openapi.json; every name, code and id is invented.
 */
import type { OrderParse, OrderParseCandidate, OrderParseLine } from "./api";

export function candidate(n: number, overrides: Partial<OrderParseCandidate> = {}): OrderParseCandidate {
  return {
    variantId: `var-${n}`,
    productId: `prod-${n}`,
    sku: `MOCK-${String(n).padStart(3, "0")}`,
    name: `Örnek Ürün ${n}`,
    unit: "ADET",
    method: "similarity",
    score: 0.8,
    ...overrides,
  };
}

export function parseLine(lineNo: number, overrides: Partial<OrderParseLine> = {}): OrderParseLine {
  return {
    lineNo,
    rawText: `${lineNo * 2} adet örnek ${lineNo}`,
    productCode: null,
    productText: `örnek ${lineNo}`,
    quantity: lineNo * 2,
    unit: "adet",
    brandModel: null,
    parseConfidence: 0.9,
    notes: null,
    matchStatus: "unmatched",
    matchConfidence: 0,
    proposedVariantId: null,
    unitMismatch: false,
    candidates: [],
    selectedVariantId: null,
    selectedQuantity: null,
    selection: null,
    ...overrides,
  };
}

/** Line 1 matched (proposal var-1), line 2 ambiguous (var-2 / var-3), line 3 unmatched. */
export function orderParse(overrides: Partial<OrderParse> = {}): OrderParse {
  const lines = overrides.lines ?? [
    parseLine(1, { matchStatus: "matched", matchConfidence: 0.95, proposedVariantId: "var-1", candidates: [candidate(1, { method: "sku", score: 1 })] }),
    parseLine(2, { matchStatus: "ambiguous", matchConfidence: 0.4, candidates: [candidate(2), candidate(3, { score: 0.7 })] }),
    parseLine(3, { matchStatus: "unmatched" }),
  ];
  return {
    id: "parse-1",
    status: "needs_review",
    channel: "whatsapp_paste",
    customerId: null,
    parser: "rules",
    model: null,
    promptVersion: "v1",
    fallbackReason: "disabled",
    messageText: lines.map((l) => l.rawText).join("\n"),
    textPurged: false,
    lines,
    unparsedFragments: [],
    warnings: ["ai_disabled"],
    summary: { lines: lines.length, matched: 1, ambiguous: 1, unmatched: 1, overallConfidence: 0.6 },
    orderId: null,
    createdAt: "2026-11-05T09:00:00Z",
    createdBy: "u-1",
    reviewedAt: null,
    reviewedBy: null,
    version: 1,
    ...overrides,
  };
}
