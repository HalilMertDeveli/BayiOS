import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { assistantErrorMessage, rateLimitMessage, translateApproveFieldMessage, translateTextError, waitText } from "./errors";
import { confidenceText, parserNotice, warningText, warningTexts } from "./labels";

describe("warnings → Turkish", () => {
  it("maps the injection warning to the fixed Turkish sentence", () => {
    expect(warningText("instruction_ignored")).toEqual({
      code: "instruction_ignored",
      text: "Mesajda talimat benzeri ifade bulundu; yok sayıldı.",
      tone: "warning",
    });
  });

  it("covers every parser and server code from the API", () => {
    const codes = [
      "price_mentioned",
      "instruction_ignored",
      "quantity_missing",
      "unit_unclear",
      "customer_info_present",
      "delivery_note",
      "reference_to_previous_order",
      "unreadable_text",
      "ai_disabled",
      "ai_quota_exceeded",
      "ai_unavailable",
      "line_not_in_message",
      "code_not_in_message",
      "catalog_truncated",
      "personal_data_masked",
    ];
    for (const code of codes) expect(warningText(code).text, code).not.toContain(code);
  });

  it("keeps an unknown code visible instead of dropping it", () => {
    expect(warningText("new_code").text).toContain("new_code");
  });

  it("drops the fallback codes (shown by the parser notice), dedupes, and puts warnings before notes", () => {
    const list = warningTexts(["personal_data_masked", "ai_quota_exceeded", "instruction_ignored", "instruction_ignored"]);
    expect(list.map((w) => w.code)).toEqual(["instruction_ignored", "personal_data_masked"]);
  });
});

describe("parserNotice", () => {
  it("AI parse", () => {
    expect(parserNotice("ai", null)).toMatchObject({ label: "AI", tone: "info" });
  });

  it("rules because AI is disabled is a neutral note", () => {
    const notice = parserNotice("rules", "disabled", ["ai_disabled"]);
    expect(notice).toMatchObject({ label: "Kural tabanlı", tone: "info" });
    expect(notice.text).toContain("AI ayrıştırma bu ortamda kapalı.");
  });

  it.each([
    ["quota_exceeded", "Bugünkü AI kullanım sınırı doldu."],
    ["provider_error", "AI hizmetine ulaşılamadı."],
    ["timeout", "AI hizmeti zamanında yanıt vermedi."],
    ["invalid_output", "AI yanıtı doğrulamadan geçmedi."],
  ])("rules as fallback (%s) is a warning that says why", (reason, text) => {
    const notice = parserNotice("rules", reason);
    expect(notice.tone).toBe("warning");
    expect(notice.text).toContain(text);
    expect(notice.text).toContain("(yedek)");
  });

  it("falls back to the server warnings when the reason is missing", () => {
    expect(parserNotice("rules", null, ["ai_quota_exceeded"]).text).toContain("kullanım sınırı");
  });
});

describe("errors", () => {
  it("429 ai.rate_limited names the wait from Retry-After", () => {
    const error = new ApiError("x", 429, { code: "ai.rate_limited" }, undefined, 1500);
    expect(rateLimitMessage(error)).toContain("25 dakika sonra");
    expect(assistantErrorMessage(error)).toBe(rateLimitMessage(error));
    expect(waitText(30)).toBe("30 saniye");
  });

  it("403 by code + permission extension, not by title", () => {
    const error = new ApiError("x", 403, { code: "auth.forbidden", title: "Missing permission: orders.create", permission: "orders.create" });
    expect(assistantErrorMessage(error)).toContain("Taslak sipariş oluşturma yetkiniz yok");
  });

  it("409 codes", () => {
    expect(assistantErrorMessage(new ApiError("x", 409, { code: "ai.parse_not_reviewable" }))).toContain("tamamlandı veya reddedildi");
    expect(assistantErrorMessage(new ApiError("x", 409, { code: "ai.parse_text_purged" }))).toContain("saklama süresi");
  });

  it("422 texts", () => {
    expect(translateTextError("At most 4000 characters.")).toBe("Mesaj en fazla 4.000 karakter olabilir; mesajı bölerek gönderin.");
    expect(translateTextError("At most 150 lines.")).toContain("150 satır");
    expect(translateApproveFieldMessage("Choose the product variant for this line.")).toBe("Bu satır için ürünü seçin.");
    expect(translateApproveFieldMessage("Variant is not active.")).toBe("Bu ürün aktif değil; siparişe eklenemez.");
  });

  it("confidence as a Turkish percent", () => {
    expect(confidenceText(0.954)).toBe("%95");
    expect(confidenceText("1")).toBe("%100");
  });
});
