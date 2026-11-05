import { describe, expect, it } from "vitest";
import {
  buildApproveRequest,
  checkQuantity,
  initialReview,
  lineErrorsFromFields,
  lineIssues,
  messageSegments,
  reviewReducer,
  reviewSummary,
  type ReviewAction,
  type ReviewState,
} from "./review";
import { candidate, orderParse, parseLine } from "./test-fixtures";

const apply = (state: ReviewState, ...actions: ReviewAction[]) => actions.reduce(reviewReducer, state);
const HEADER = { customerId: "cust-1", warehouseId: "wh-1", notes: "" };

describe("initialReview", () => {
  it("pre-fills only the matched line's proposal, unconfirmed; ambiguous and unmatched lines start empty", () => {
    const state = initialReview(orderParse());
    expect(state.lines.map((l) => [l.lineNo, l.variant?.id ?? null, l.variant?.source ?? null, l.confirmed])).toEqual([
      [1, "var-1", "proposed", false],
      [2, null, null, false],
      [3, null, null, false],
    ]);
  });

  it("never pre-selects an ambiguous line, even with a single high-scoring candidate", () => {
    const parse = orderParse({ lines: [parseLine(1, { matchStatus: "ambiguous", candidates: [candidate(9, { score: 0.99 })] })] });
    expect(initialReview(parse).lines[0].variant).toBeNull();
  });

  it("ignores a proposal id on a line that is not matched", () => {
    const parse = orderParse({ lines: [parseLine(1, { matchStatus: "ambiguous", proposedVariantId: "var-1", candidates: [candidate(1)] })] });
    expect(initialReview(parse).lines[0].variant).toBeNull();
  });

  it("takes the parsed quantity as text (Turkish decimal comma) and leaves a missing one empty", () => {
    const parse = orderParse({ lines: [parseLine(1, { quantity: 2.5 }), parseLine(2, { quantity: null })] });
    expect(initialReview(parse).lines.map((l) => l.quantityText)).toEqual(["2,5", ""]);
  });
});

describe("confirm gating", () => {
  it("is blocked until the proposal is confirmed, the ambiguous line chosen and the unmatched line resolved", () => {
    let state = initialReview(orderParse());
    expect(reviewSummary(state)).toMatchObject({ canConfirm: false, pending: 3 });

    state = apply(state, { type: "confirmProposal", lineNo: 1, confirmed: true });
    expect(reviewSummary(state).pending).toBe(2);

    state = apply(state, { type: "choose", lineNo: 2, source: "candidate", variant: { id: "var-3", sku: "MOCK-003", name: "Örnek Ürün 3", unit: "ADET" } });
    expect(reviewSummary(state).pending).toBe(1);

    state = apply(state, { type: "include", lineNo: 3, include: false });
    expect(reviewSummary(state)).toMatchObject({ canConfirm: true, kept: 2, skipped: 1, pending: 0, blocker: null });
  });

  it("an unmatched line can be resolved by a catalog search instead of being removed", () => {
    const state = apply(
      initialReview(orderParse()),
      { type: "confirmProposal", lineNo: 1, confirmed: true },
      { type: "choose", lineNo: 2, source: "candidate", variant: { id: "var-2", sku: "MOCK-002", name: null, unit: "ADET" } },
      { type: "choose", lineNo: 3, source: "manual", variant: { id: "var-7", sku: "MOCK-007", name: null, unit: null } },
    );
    expect(reviewSummary(state).canConfirm).toBe(true);
    expect(state.lines[2]).toMatchObject({ variant: { id: "var-7", source: "manual" }, confirmed: true });
  });

  it("clearing a variant unconfirms the line; confirming applies only to a proposal", () => {
    let state = apply(initialReview(orderParse()), { type: "confirmProposal", lineNo: 1, confirmed: true }, { type: "clearVariant", lineNo: 1 });
    expect(state.lines[0]).toMatchObject({ variant: null, confirmed: false });
    state = apply(state, { type: "confirmProposal", lineNo: 1, confirmed: true });
    expect(state.lines[0].confirmed).toBe(false);
  });

  it("removing every line blocks the confirm (reject instead)", () => {
    const state = apply(
      initialReview(orderParse()),
      { type: "include", lineNo: 1, include: false },
      { type: "include", lineNo: 2, include: false },
      { type: "include", lineNo: 3, include: false },
    );
    expect(reviewSummary(state)).toMatchObject({ canConfirm: false, kept: 0 });
    expect(reviewSummary(state).blocker).toMatch(/reddedin/);
  });

  it("a line without quantity blocks until a valid quantity is typed", () => {
    const parse = orderParse({ lines: [parseLine(1, { matchStatus: "matched", proposedVariantId: "var-1", candidates: [candidate(1)], quantity: null })] });
    let state = apply(initialReview(parse), { type: "confirmProposal", lineNo: 1, confirmed: true });
    expect(lineIssues(state.lines[0]).map((i) => i.kind)).toEqual(["quantity"]);
    state = apply(state, { type: "quantity", lineNo: 1, text: "0" });
    expect(reviewSummary(state).canConfirm).toBe(false);
    state = apply(state, { type: "quantity", lineNo: 1, text: "12,5" });
    expect(reviewSummary(state).canConfirm).toBe(true);
  });
});

describe("checkQuantity", () => {
  it.each([
    ["3", 3],
    ["2,5", 2.5],
    ["0.125", 0.125],
    ["1000000", 1_000_000],
  ])("accepts %s", (text, value) => {
    expect(checkQuantity(text)).toEqual({ ok: true, value });
  });

  it.each(["", "0", "-1", "1,2345", "1.000.000", "abc", "1000000,5", "1e3"])("rejects %j", (text) => {
    expect(checkQuantity(text).ok).toBe(false);
  });
});

describe("buildApproveRequest", () => {
  it("lists every line once; kept lines name variant and quantity explicitly, removed lines include=false", () => {
    const state = apply(
      initialReview(orderParse()),
      { type: "confirmProposal", lineNo: 1, confirmed: true },
      { type: "quantity", lineNo: 1, text: "4" },
      { type: "choose", lineNo: 2, source: "candidate", variant: { id: "var-3", sku: "MOCK-003", name: null, unit: "ADET" } },
      { type: "include", lineNo: 3, include: false },
    );
    expect(buildApproveRequest(state, { ...HEADER, notes: "  Kapıya bırakın  " })).toEqual({
      customerId: "cust-1",
      warehouseId: "wh-1",
      notes: "Kapıya bırakın",
      lines: [
        { lineNo: 1, include: true, variantId: "var-1", quantity: 4 },
        { lineNo: 2, include: true, variantId: "var-3", quantity: 4 },
        { lineNo: 3, include: false, variantId: null, quantity: null },
      ],
    });
  });

  it("refuses an incomplete review (no implicit acceptance)", () => {
    expect(() => buildApproveRequest(initialReview(orderParse()), HEADER)).toThrow();
  });

  it("never carries a price field", () => {
    const state = apply(initialReview(orderParse({ lines: [orderParse().lines[0]] })), { type: "confirmProposal", lineNo: 1, confirmed: true });
    const body = JSON.stringify(buildApproveRequest(state, HEADER));
    expect(body).not.toMatch(/price|stock/i);
  });
});

describe("lineErrorsFromFields", () => {
  it("maps request indexes back to line numbers", () => {
    const state = initialReview(orderParse());
    const errors = lineErrorsFromFields(state, { "lines[1].variantId": "Ürünü seçin.", "lines[2].quantity": "Miktar girin.", customerId: "x" });
    expect([...errors.entries()]).toEqual([
      [2, ["Ürünü seçin."]],
      [3, ["Miktar girin."]],
    ]);
  });
});

describe("messageSegments", () => {
  it("marks each line's raw text in order and keeps everything else as plain text", () => {
    const message = "Merhaba\n2 adet H4 ampul\n2 adet H7 ampul\nteşekkürler";
    const segments = messageSegments(message, [
      { lineNo: 1, rawText: "2 adet H4 ampul" },
      { lineNo: 2, rawText: "2 adet H7 ampul" },
    ]);
    expect(segments).toEqual([
      { text: "Merhaba\n", lineNo: null },
      { text: "2 adet H4 ampul", lineNo: 1 },
      { text: "\n", lineNo: null },
      { text: "2 adet H7 ampul", lineNo: 2 },
      { text: "\nteşekkürler", lineNo: null },
    ]);
    expect(segments.map((s) => s.text).join("")).toBe(message);
  });

  it("finds repeated texts in order and skips a line whose text is not in the message", () => {
    const segments = messageSegments("1 adet X\n1 adet X", [
      { lineNo: 1, rawText: "1 adet X" },
      { lineNo: 2, rawText: "1 adet X" },
      { lineNo: 3, rawText: "yok" },
    ]);
    expect(segments.filter((s) => s.lineNo !== null).map((s) => s.lineNo)).toEqual([1, 2]);
  });
});
