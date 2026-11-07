import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ToastProvider } from "@/components/ui/toast";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import type { OrderParse } from "@/lib/assistant/api";
import { candidate, orderParse, parseLine } from "@/lib/assistant/test-fixtures";
import { SessionContext } from "@/lib/session/session-context";
import type { FixtureRole } from "@/test/me-fixtures";
import { mockSession } from "@/test/session";
import { MessageText } from "./message-text";
import { OrderParseReviewPage } from "./order-parse-review";

const push = vi.fn();
vi.mock("next/navigation", () => ({ usePathname: () => "/siparisler/whatsapp/parse-1", useRouter: () => ({ replace: vi.fn(), push }) }));

afterEach(() => push.mockReset());

/** MOCK API: the parse, its approve, one customer; anything else 404 (warehouses degrade to the default). */
function fakeApi(parse: OrderParse) {
  const calls: { path: string; options: RequestOptions }[] = [];
  const api: ApiClient = {
    async request<T>(path: string, options: RequestOptions = {}) {
      calls.push({ path, options });
      if (path === "/api/v1/ai/order-parses/parse-1" && !options.method) return { data: parse as T, status: 200, etag: 'W/"1"' };
      if (path === "/api/v1/ai/order-parses/parse-1/approve") {
        return { data: { ...parse, status: "approved", orderId: "ord-9", version: 2 } as T, status: 200, etag: 'W/"2"' };
      }
      if (path === "/api/v1/customers/cust-1") {
        return { data: { customer: { id: "cust-1", name: "Mock Müşteri", code: "C-1" } } as T, status: 200, etag: 'W/"1"' };
      }
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  return { api, calls };
}

function renderReview(parse: OrderParse, role: FixtureRole = "owner") {
  const { api, calls } = fakeApi(parse);
  render(
    <SessionContext value={mockSession(role, { api })}>
      <ToastProvider>
        <OrderParseReviewPage parseId="parse-1" />
      </ToastProvider>
    </SessionContext>,
  );
  return { calls };
}

const confirmButton = () => screen.getByRole("button", { name: "Taslak siparişi oluştur" });

describe("MessageText (untrusted text)", () => {
  it("renders HTML, script and markdown as inert characters, without links", () => {
    const message = '<img src=x onerror="alert(1)"><script>alert(2)</script> **kalın** [tıkla](https://evil.example) https://evil.example\n2 adet <b>H4</b>';
    const { container } = render(<MessageText message={message} lines={[{ lineNo: 1, rawText: "2 adet <b>H4</b>" }]} />);
    const node = screen.getByTestId("whatsapp-message");
    expect(node.textContent).toBe(message.replace("2 adet", "Satır 12 adet"));
    expect(container.querySelector("img, script, b, a, strong")).toBeNull();
    expect(within(node).getByText("2 adet <b>H4</b>").tagName).toBe("MARK");
  });
});

describe("OrderParseReviewPage", () => {
  it("shows the parser, the injection warning in Turkish, and no prices", async () => {
    renderReview(orderParse({ warnings: ["ai_disabled", "instruction_ignored", "price_mentioned"] }));
    expect(await screen.findByText("Ayrıştırıcı: Kural tabanlı")).toBeTruthy();
    expect(screen.getByText(/Mesajda talimat benzeri ifade bulundu; yok sayıldı\./)).toBeTruthy();
    expect(screen.getByText(/Mesajda fiyat geçiyor; dikkate alınmadı/)).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/₺|TRY|stokta/);
  });

  it("ambiguous candidates are never pre-selected and confirm stays disabled until every kept line is resolved", async () => {
    const user = userEvent.setup();
    const { calls } = renderReview(orderParse({ customerId: "cust-1" }));
    const radios = await screen.findAllByRole("radio");
    expect(radios).toHaveLength(2);
    for (const radio of radios) expect((radio as HTMLInputElement).checked).toBe(false);
    expect((confirmButton() as HTMLButtonElement).disabled).toBe(true);

    await user.click(screen.getByLabelText("Satır 1: önerilen ürünü onaylıyorum"));
    await user.click(screen.getByRole("radio", { name: /MOCK-003/ }));
    expect((confirmButton() as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("1 satır seçim, onay veya miktar bekliyor.")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Satır 3: siparişten çıkar" }));
    expect((confirmButton() as HTMLButtonElement).disabled).toBe(false);

    await user.click(confirmButton());
    const approve = calls.find((c) => c.path.endsWith("/approve"))!;
    expect(approve.options.ifMatch).toBe('W/"1"');
    expect(approve.options.idempotencyKey).toBeTruthy();
    expect(approve.options.body).toEqual({
      customerId: "cust-1",
      warehouseId: null,
      notes: null,
      lines: [
        { lineNo: 1, include: true, variantId: "var-1", quantity: 2, unit: "ADET" },
        { lineNo: 2, include: true, variantId: "var-3", quantity: 4, unit: "ADET" },
        { lineNo: 3, include: false, variantId: null, quantity: null },
      ],
    });
    expect(push).toHaveBeenCalledWith("/siparisler/ord-9");
  });

  it("an invalid quantity blocks the confirm and shows a field error", async () => {
    const user = userEvent.setup();
    const parse = orderParse({ customerId: "cust-1", lines: [parseLine(1, { matchStatus: "matched", proposedVariantId: "var-1", candidates: [candidate(1)] })] });
    renderReview(parse);
    await user.click(await screen.findByLabelText("Satır 1: önerilen ürünü onaylıyorum"));
    expect((confirmButton() as HTMLButtonElement).disabled).toBe(false);
    const quantity = screen.getByLabelText("Satır 1 miktarı");
    await user.clear(quantity);
    await user.type(quantity, "1,2345");
    expect(screen.getByText("En fazla 3 ondalık basamak girin.")).toBeTruthy();
    expect((confirmButton() as HTMLButtonElement).disabled).toBe(true);
  });

  it("requires a customer when none was given on submit", async () => {
    const user = userEvent.setup();
    const parse = orderParse({ lines: [parseLine(1, { matchStatus: "matched", proposedVariantId: "var-1", candidates: [candidate(1)] })] });
    renderReview(parse);
    await user.click(await screen.findByLabelText("Satır 1: önerilen ürünü onaylıyorum"));
    expect((confirmButton() as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("Müşteri seçin.")).toBeTruthy();
  });

  it("an approved parse links to the draft order", async () => {
    renderReview(orderParse({ status: "approved", orderId: "ord-9" }));
    expect(await screen.findByRole("link", { name: "Taslak siparişi aç" })).toHaveProperty("href", expect.stringContaining("/siparisler/ord-9"));
  });

  it("viewer gets the locked state and the API is not called", () => {
    const { calls } = renderReview(orderParse(), "viewer");
    expect(screen.getByText("WhatsApp siparişi yetkiniz yok")).toBeTruthy();
    expect(calls).toHaveLength(0);
  });

  it("a parse still being processed shows progress, polls the status and opens the review when ready", async () => {
    const ready = orderParse({ customerId: "cust-1" });
    let current: OrderParse = { ...ready, status: "pending", parser: null, promptVersion: null, lines: [], completedAt: null };
    const api: ApiClient = {
      async request<T>(path: string) {
        if (path === "/api/v1/ai/order-parses/parse-1") return { data: current as T, status: 200, etag: 'W/"1"' };
        if (path === "/api/v1/ai/order-parses/parse-1/status") {
          current = ready; // MOCK: the background job finished between two polls
          return { data: { id: "parse-1", state: "succeeded", parseStatus: "needs_review", failureCode: null, resultUrl: "/x", pollAfterMs: null } as T, status: 200, etag: null };
        }
        throw new ApiError("not mocked", 404, { code: "resource.not_found" });
      },
    };
    render(
      <SessionContext value={mockSession("owner", { api })}>
        <ToastProvider>
          <OrderParseReviewPage parseId="parse-1" />
        </ToastProvider>
      </SessionContext>,
    );
    expect(await screen.findByText("Mesaj ayrıştırılıyor")).toBeTruthy();
    expect(await screen.findByRole("button", { name: "Taslak siparişi oluştur" }, { timeout: 3000 })).toBeTruthy();
  });

  it("a parse that failed in the background offers parsing again or the manual order", async () => {
    renderReview(orderParse({ status: "failed", failureCode: "ai.opt_in_revoked", parser: null, promptVersion: null, lines: [] }));
    expect(await screen.findByRole("heading", { name: "Mesaj ayrıştırılamadı" })).toBeTruthy();
    expect(screen.getByText(/mesaj hiçbir yere gönderilmedi/)).toBeTruthy();
    expect(screen.getByRole("link", { name: "Siparişi elle oluştur" }).getAttribute("href")).toBe("/siparisler/yeni");
  });

  it("uses the warehouse given on submit as the default of the draft", async () => {
    const user = userEvent.setup();
    const parse = orderParse({
      customerId: "cust-1",
      warehouseId: "wh-7",
      lines: [parseLine(1, { matchStatus: "matched", proposedVariantId: "var-1", candidates: [candidate(1)] })],
    });
    const { calls } = renderReview(parse);
    await user.click(await screen.findByLabelText("Satır 1: önerilen ürünü onaylıyorum"));
    await user.click(confirmButton());
    expect(calls.find((c) => c.path.endsWith("/approve"))?.options.body).toMatchObject({ warehouseId: "wh-7" });
  });
});
