import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import type { FixturePlan, FixtureRole } from "@/test/me-fixtures";
import { mockSession } from "@/test/session";
import { WhatsappOrderPage, messageError } from "./whatsapp-order-entry";

const push = vi.fn();
vi.mock("next/navigation", () => ({ usePathname: () => "/siparisler/whatsapp", useRouter: () => ({ replace: vi.fn(), push }) }));

afterEach(() => push.mockReset());

/** MOCK 202 of a submit (pollAfterMs at the client's minimum, so tests poll quickly). */
const accepted = () => ({
  data: { id: "parse-1", status: "pending", statusUrl: "/api/v1/ai/order-parses/parse-1/status", resultUrl: "/api/v1/ai/order-parses/parse-1", pollAfterMs: 100 },
  status: 202,
  etag: null,
});

const status = (state: "pending" | "succeeded" | "failed", failureCode: string | null = null) => ({
  id: "parse-1",
  state,
  parseStatus: state === "succeeded" ? "needs_review" : state === "failed" ? "failed" : "pending",
  failureCode,
  resultUrl: "/api/v1/ai/order-parses/parse-1",
  pollAfterMs: state === "pending" ? 100 : null,
  createdAt: "2026-11-05T09:00:00Z",
  completedAt: null,
});

const aiSettings = (enabled: boolean) => ({
  aiParsingEnabled: enabled,
  providerConfigured: false,
  limits: { maxInputChars: 4000, maxInputLines: 150, hourlyParseLimit: 30 },
  updatedAt: null,
  updatedBy: null,
  version: 1,
});

/**
 * MOCK API: the pending list is empty; AI opt-in on unless `optedIn` is false; submit answers with `onSubmit`; the
 * status endpoint answers from `statuses` in order (the last one repeats).
 */
function renderEntry(
  role: FixtureRole,
  onSubmit: () => unknown = accepted,
  plan: FixturePlan = "trial",
  options: { optedIn?: boolean; statuses?: ReturnType<typeof status>[] } = {},
) {
  const calls: { path: string; options: RequestOptions }[] = [];
  let optedIn = options.optedIn ?? true;
  const statuses = [...(options.statuses ?? [status("pending"), status("succeeded")])];
  const api: ApiClient = {
    async request<T>(path: string, request: RequestOptions = {}) {
      calls.push({ path, options: request });
      const ok = (data: unknown, code = 200) => ({ data: data as T, status: code, etag: null });
      if (path.startsWith("/api/v1/ai/order-parses?")) return ok({ items: [] });
      if (path === "/api/v1/ai/settings" && request.method === "PUT") {
        optedIn = Boolean((request.body as { aiParsingEnabled: boolean }).aiParsingEnabled);
        return ok(aiSettings(optedIn));
      }
      if (path === "/api/v1/ai/settings") return ok(aiSettings(optedIn));
      if (path === "/api/v1/ai/order-parses" && request.method === "POST") return onSubmit() as { data: T; status: number; etag: string | null };
      if (path === "/api/v1/ai/order-parses/parse-1/status") return ok(statuses.length > 1 ? statuses.shift() : statuses[0]);
      throw new ApiError("not mocked", 404, { code: "resource.not_found" });
    },
  };
  render(
    <SessionContext value={mockSession(role, { api, plan })}>
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
  it("sends the trimmed text with an Idempotency-Key, polls the status (202) and opens the review when it is ready", async () => {
    const user = userEvent.setup();
    const { calls } = renderEntry("sales");
    expect(await screen.findByText("Bekleyen inceleme yok")).toBeTruthy();
    await user.type(await screen.findByRole("textbox", { name: /^WhatsApp mesajı/ }), "  2 adet H4 ampul  ");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    const submit = calls.find((c) => c.options.method === "POST")!;
    expect(submit.options.body).toEqual({ text: "2 adet H4 ampul", customerId: null });
    expect(submit.options.idempotencyKey).toBeTruthy();
    expect(await screen.findByText("Mesaj ayrıştırılıyor ve kataloğunuzla eşleştiriliyor…")).toBeTruthy();
    await waitFor(() => expect(push).toHaveBeenCalledWith("/siparisler/whatsapp/parse-1"), { timeout: 3000 });
    expect(calls.filter((c) => c.path.endsWith("/status"))).toHaveLength(2); // pending, then succeeded
  });

  it("a parse that failed in the background is explained, the text stays and the manual way is offered", async () => {
    const user = userEvent.setup();
    renderEntry("owner", accepted, "trial", { statuses: [status("failed", "ai.parse_timed_out")] });
    await user.type(await screen.findByRole("textbox", { name: /^WhatsApp mesajı/ }), "2 adet H4 ampul");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    const alert = await screen.findByRole("alert", {}, { timeout: 3000 });
    expect(alert.textContent).toContain("Mesaj zamanında işlenemedi.");
    expect(screen.getByRole("link", { name: "Siparişi elle oluştur" }).getAttribute("href")).toBe("/siparisler/yeni");
    expect((screen.getByRole("textbox", { name: /^WhatsApp mesajı/ }) as HTMLTextAreaElement).value).toBe("2 adet H4 ampul");
    expect(push).not.toHaveBeenCalled();
  });

  it("AI opt-in off (default): explained instead of the form; the owner switches it on", async () => {
    const user = userEvent.setup();
    const { calls } = renderEntry("owner", accepted, "trial", { optedIn: false });
    expect(await screen.findByRole("heading", { name: "AI ile sipariş ayrıştırma kapalı" })).toBeTruthy();
    expect(screen.queryByRole("textbox", { name: /^WhatsApp mesajı/ })).toBeNull();
    await user.click(screen.getByRole("button", { name: "AI ayrıştırmayı aç" }));
    expect(await screen.findByRole("textbox", { name: /^WhatsApp mesajı/ })).toBeTruthy();
    expect(calls.find((c) => c.options.method === "PUT")?.options.body).toEqual({ aiParsingEnabled: true });
  });

  it("AI opt-in off: sales cannot switch it and is pointed to the manual order", async () => {
    renderEntry("sales", accepted, "trial", { optedIn: false });
    expect(await screen.findByRole("heading", { name: "AI ile sipariş ayrıştırma kapalı" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "AI ayrıştırmayı aç" })).toBeNull();
    expect(screen.getByRole("note").textContent).toContain("işletme sahibi veya yöneticisi");
  });

  it("409 ai.opt_in_required on submit (switched off meanwhile) shows the opt-in panel", async () => {
    const user = userEvent.setup();
    renderEntry("sales", () => {
      throw new ApiError("x", 409, { code: "ai.opt_in_required" });
    });
    await user.type(await screen.findByRole("textbox", { name: /^WhatsApp mesajı/ }), "2 adet H4 ampul");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    expect(await screen.findByRole("heading", { name: "AI ile sipariş ayrıştırma kapalı" })).toBeTruthy();
  });

  it("429 ai.rate_limited: clear message with the wait; the text stays", async () => {
    const user = userEvent.setup();
    renderEntry("owner", () => {
      throw new ApiError("x", 429, { code: "ai.rate_limited" }, undefined, 600);
    });
    await user.type(await screen.findByRole("textbox", { name: /^WhatsApp mesajı/ }), "2 adet H4 ampul");
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
    await user.type(await screen.findByRole("textbox", { name: /^WhatsApp mesajı/ }), "2 adet H4 ampul");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    expect(await screen.findByText("Mesaj en fazla 2.000 karakter olabilir; mesajı bölerek gönderin.")).toBeTruthy();
  });

  it.each<FixtureRole>(["viewer", "warehouse_staff"])("%s has no whatsapp.parse: locked, nothing is requested", (role) => {
    const { calls } = renderEntry(role);
    expect(screen.getByText("WhatsApp siparişi yetkiniz yok")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Mesajı ayrıştır" })).toBeNull();
    expect(calls).toHaveLength(0);
  });

  it("without the AI assistant add-on (MOCK plan): parsing is locked, started reviews stay listed", async () => {
    const { calls } = renderEntry("owner", undefined, "professional");
    expect(screen.queryByRole("button", { name: "Mesajı ayrıştır" })).toBeNull();
    const note = screen.getByRole("note");
    expect(note.textContent).toContain("AI WhatsApp asistanı modülünde");
    expect(screen.getByRole("link", { name: "Paket ve modülleri gör" }).getAttribute("href")).toBe("/ayarlar/paket");
    expect(await screen.findByRole("heading", { name: "İnceleme bekleyen mesajlar" })).toBeTruthy();
    expect(calls.some((c) => c.path.startsWith("/api/v1/ai/order-parses?"))).toBe(true);
  });

  it("an API 403 entitlement.limit_exceeded (monthly AI quota) is explained", async () => {
    const user = userEvent.setup();
    renderEntry("owner", () => {
      throw new ApiError("quota", 403, {
        code: "entitlement.limit_exceeded",
        feature: "ai_parses_per_month",
        limit: 1000,
        current: 1000,
        suggestedPlan: null,
        suggestedModule: "ai_assistant",
      });
    });
    await user.type(await screen.findByRole("textbox", { name: /WhatsApp mesajı/ }), "2 adet H4 ampul");
    await user.click(screen.getByRole("button", { name: "Mesajı ayrıştır" }));
    expect((await screen.findByRole("alert")).textContent).toContain("Aylık AI ayrıştırma sınırına ulaşıldı (1.000/1.000).");
  });
});
