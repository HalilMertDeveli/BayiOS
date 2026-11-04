import { describe, expect, it } from "vitest";
import { dateRangeError, emptyOrderQuery, hasOrderCriteria, parseOrderQuery, toOrderApiQuery, toOrderSearchString } from "./list-query";

const CUSTOMER = "00000000-0000-4000-8000-7c0000000001";

describe("order list query", () => {
  it("round-trips through the URL with Turkish parameter names", () => {
    const query = { q: "0001", status: "pending_approval", customerId: CUSTOMER, source: "manual", from: "2026-11-01", to: "2026-11-05" };
    const search = toOrderSearchString(query);
    expect(search).toBe(`q=0001&durum=pending_approval&musteri=${CUSTOMER}&kaynak=manual&baslangic=2026-11-01&bitis=2026-11-05`);
    expect(parseOrderQuery(new URLSearchParams(search))).toEqual(query);
  });

  it("drops values the API would reject", () => {
    expect(parseOrderQuery(new URLSearchParams("durum=nope&musteri=abc&kaynak=fax&baslangic=01.11.2026&bitis=x"))).toEqual(emptyOrderQuery);
  });

  it("maps a status group to the API's comma-separated status", () => {
    const api = new URLSearchParams(toOrderApiQuery({ ...emptyOrderQuery, status: "hazirlaniyor" }));
    expect(api.get("status")).toBe("approved,picking,packed");
    expect(api.get("limit")).toBe("50");
    expect(api.has("cursor")).toBe(false);
  });

  it("sends every filter and the cursor", () => {
    const api = new URLSearchParams(
      toOrderApiQuery({ q: "12", status: "draft", customerId: CUSTOMER, source: "whatsapp_ai", from: "2026-11-01", to: "2026-11-02" }, "c1", 20),
    );
    expect(Object.fromEntries(api)).toEqual({
      status: "draft",
      customerId: CUSTOMER,
      source: "whatsapp_ai",
      from: "2026-11-01",
      to: "2026-11-02",
      q: "12",
      limit: "20",
      cursor: "c1",
    });
  });

  it("flags a reversed date range and detects criteria", () => {
    expect(dateRangeError({ from: "2026-11-05", to: "2026-11-01" })).toMatch(/sonra olamaz/);
    expect(dateRangeError({ from: "2026-11-01", to: "2026-11-01" })).toBeNull();
    expect(hasOrderCriteria(emptyOrderQuery)).toBe(false);
    expect(hasOrderCriteria({ ...emptyOrderQuery, source: "manual" })).toBe(true);
  });
});
