import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import { orderParse } from "@/lib/assistant/test-fixtures";
import { SessionContext } from "@/lib/session/session-context";
import type { FixtureRole } from "@/test/me-fixtures";
import { mockSession } from "@/test/session";
import { WhatsappOrderPage, messageError } from "./whatsapp-order-entry";

const push = vi.fn();
vi.mock("next/navigation", () => ({ usePathname: () => "/siparisler/whatsapp", useRouter: () => ({ replace: vi.fn(), push }) }));

afterEach(() => push.mockReset());

/** MOCK API: the pending list is empty; submit answers with `onSubmit`. */
function renderEntry(role: FixtureRole, onSubmit: () => unknown = () => ({ data: orderParse(), status: 201, etag: 'W/"1"' })) {
  const calls: { path: string; options: RequestOptions }[] = [];
  const api: ApiClient = {
    async request<T>(path: string, options: RequestOptions = {}) {
      calls.push({ path, options });
      if (path.startsWith("/api/v1/ai/order-parses?")) return { data: { items: [] } as T, status: 200, etag: null };
      if (path === "/api/v1/ai/order-parses" && options.method === "POST") return onSubmit() as { data: T; status: number; etag: string | null };
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  render(
    <SessionContext value={mockSession(role, { api })}>
      <WhatsappOrderPage />
    </SessionContext>,
  );
  return { calls };
}

describe("messageError", () => {
  it("checks empty, character and line limits", () => {
    expect(messageError("   ")).toBe("WhatsApp mesajını yapıştırın.");
    expect(messageError("a".repeat(4001))).toContain("4.000 karakter");
    expect(messageError(Array.from({ length: 151 }, () => "x").join("\n"))).toContain("150 satır");
    expect(messageError("2 adet H4 ampul")).toBeNull();
  });
});

describe("WhatsappOrderPage", () => {
  it("sends the trimmed text with an Idempotency-Key and opens the review", async () => {
    const user = userEvent.setup();
    const { calls } = renderEntry("sales");
    expect(await screen.findByText("Bekleyen inceleme yok")).toBeTruthy();
    await user.type(screen.getByRole("textbox", { name: /^WhatsApp mesajı/ }), "  2 adet H4 ampul  ");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    const submit = calls.find((c) => c.options.method === "POST")!;
    expect(submit.options.body).toEqual({ text: "2 adet H4 ampul", customerId: null });
    expect(submit.options.idempotencyKey).toBeTruthy();
    expect(push).toHaveBeenCalledWith("/siparisler/whatsapp/parse-1");
  });

  it("429 ai.rate_limited: clear message with the wait; the text stays", async () => {
    const user = userEvent.setup();
    renderEntry("owner", () => {
      throw new ApiError("x", 429, { code: "ai.rate_limited" }, undefined, 600);
    });
    await user.type(screen.getByRole("textbox", { name: /^WhatsApp mesajı/ }), "2 adet H4 ampul");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    expect(await screen.findByText("Saatlik ayrıştırma sınırı doldu")).toBeTruthy();
    expect(screen.getByText(/10 dakika sonra tekrar deneyin/)).toBeTruthy();
    expect((screen.getByRole("textbox", { name: /^WhatsApp mesajı/ }) as HTMLTextAreaElement).value).toBe("2 adet H4 ampul");
    expect(push).not.toHaveBeenCalled();
  });

  it("422 text from the API is shown on the field in Turkish", async () => {
    const user = userEvent.setup();
    renderEntry("owner", () => {
      throw new ApiError("x", 422, { code: "validation.failed", errors: { text: ["At most 2000 characters."] } });
    });
    await user.type(screen.getByRole("textbox", { name: /^WhatsApp mesajı/ }), "2 adet H4 ampul");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    expect(await screen.findByText("Mesaj en fazla 2.000 karakter olabilir; mesajı bölerek gönderin.")).toBeTruthy();
  });

  it.each<FixtureRole>(["viewer", "warehouse_staff"])("%s has no whatsapp.parse: locked, nothing is requested", (role) => {
    const { calls } = renderEntry(role);
    expect(screen.getByText("WhatsApp siparişi yetkiniz yok")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Mesajı ayrıştır" })).toBeNull();
    expect(calls).toHaveLength(0);
  });
});
