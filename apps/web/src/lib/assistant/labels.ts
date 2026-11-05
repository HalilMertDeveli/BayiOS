/**
 * Turkish texts for the codes of the WhatsApp order assistant (services/api Integrations: ParseWarnings, ParserKinds,
 * FallbackReasons, MatchStatuses, MatchMethods). The API sends codes only; the web never shows English developer text.
 */

export type WarningTone = "warning" | "info";

export type WarningText = { code: string; text: string; tone: WarningTone };

/** Parser codes (may come from the model) and server codes (added by BayiOS only). */
const WARNINGS: Record<string, { text: string; tone: WarningTone }> = {
  // Parser codes
  price_mentioned: {
    text: "Mesajda fiyat geçiyor; dikkate alınmadı. Fiyatlar taslak oluşunca müşterinin fiyat listesinden gelir.",
    tone: "info",
  },
  instruction_ignored: { text: "Mesajda talimat benzeri ifade bulundu; yok sayıldı.", tone: "warning" },
  quantity_missing: { text: "Bazı satırlarda miktar yok; miktarı siz girin.", tone: "warning" },
  unit_unclear: { text: "Bazı satırlarda birim anlaşılamadı; miktarı ürünün birimine göre kontrol edin.", tone: "warning" },
  customer_info_present: { text: "Mesajda müşteri bilgisi var; müşteriyi siz seçin.", tone: "info" },
  delivery_note: { text: "Mesajda teslimat notu var; gerekirse sipariş notuna yazın.", tone: "info" },
  reference_to_previous_order: {
    text: "Mesaj önceki bir siparişe atıf yapıyor (ör. “geçen seferki gibi”); bu kısım otomatik çözülmez, ürünü siz seçin.",
    tone: "warning",
  },
  unreadable_text: { text: "Mesajın bir kısmı okunamadı; okunamayan kısımları kontrol edin.", tone: "warning" },
  // Server codes
  ai_disabled: { text: "AI ayrıştırma kapalı; mesaj kural tabanlı ayrıştırıcıyla okundu.", tone: "info" },
  ai_quota_exceeded: {
    text: "Bugünkü AI kullanım sınırı doldu; mesaj kural tabanlı ayrıştırıcıyla okundu. Satırları dikkatle kontrol edin.",
    tone: "warning",
  },
  ai_unavailable: {
    text: "AI hizmeti şu an yanıt vermedi; mesaj kural tabanlı ayrıştırıcıyla okundu. Satırları dikkatle kontrol edin.",
    tone: "warning",
  },
  line_not_in_message: { text: "Mesajda geçmeyen bir satır önerildi ve çıkarıldı.", tone: "warning" },
  code_not_in_message: { text: "Mesajda geçmeyen bir ürün kodu önerildi ve çıkarıldı.", tone: "warning" },
  catalog_truncated: {
    text: "Katalog çok büyük; benzerlik araması kataloğun bir kısmında yapıldı. Eşleşmeyen satırlarda ürünü arayarak seçin.",
    tone: "warning",
  },
  personal_data_masked: { text: "Telefon numarası ve e-posta adresleri ayrıştırmadan önce gizlendi.", tone: "info" },
};

/** Turkish text for a warning code; an unknown code still shows (neutral text, code kept for support). */
export function warningText(code: string): WarningText {
  const known = WARNINGS[code];
  return known ? { code, ...known } : { code, text: `Ayrıştırma uyarısı (${code}). Satırları dikkatle kontrol edin.`, tone: "warning" };
}

/**
 * Warnings in display order: warnings first, then notes; the fallback codes (ai_disabled / ai_quota_exceeded /
 * ai_unavailable) are left out because the parser notice already says the same.
 */
export function warningTexts(codes: readonly string[]): WarningText[] {
  const seen = new Set<string>();
  const result: WarningText[] = [];
  for (const code of codes) {
    if (seen.has(code) || FALLBACK_WARNINGS.has(code)) continue;
    seen.add(code);
    result.push(warningText(code));
  }
  return result.sort((a, b) => (a.tone === b.tone ? 0 : a.tone === "warning" ? -1 : 1));
}

const FALLBACK_WARNINGS = new Set(["ai_disabled", "ai_quota_exceeded", "ai_unavailable"]);

const FALLBACK_REASONS: Record<string, string> = {
  disabled: "AI ayrıştırma bu ortamda kapalı.",
  quota_exceeded: "Bugünkü AI kullanım sınırı doldu.",
  provider_error: "AI hizmetine ulaşılamadı.",
  timeout: "AI hizmeti zamanında yanıt vermedi.",
  refused: "AI hizmeti mesajı işlemedi.",
  truncated: "AI yanıtı yarım kaldı.",
  invalid_output: "AI yanıtı doğrulamadan geçmedi.",
};

export type ParserNotice = {
  /** Short label for the badge: "AI" or "Kural tabanlı". */
  label: string;
  /** One sentence on which parser ran and, for the rules parser, why. */
  text: string;
  tone: WarningTone;
};

/** Which parser ran and why (fallbackReason; the server warnings as a fallback when the reason is missing). */
export function parserNotice(parser: string, fallbackReason: string | null, warnings: readonly string[] = []): ParserNotice {
  if (parser === "ai") {
    return { label: "AI", text: "Mesaj AI ile okundu ve yalnızca kataloğunuzla eşleştirildi. Her satırı kontrol edip onaylayın.", tone: "info" };
  }
  const reason =
    (fallbackReason && FALLBACK_REASONS[fallbackReason]) ||
    (warnings.includes("ai_quota_exceeded")
      ? FALLBACK_REASONS.quota_exceeded
      : warnings.includes("ai_unavailable")
        ? FALLBACK_REASONS.provider_error
        : warnings.includes("ai_disabled")
          ? FALLBACK_REASONS.disabled
          : null);
  const disabled = fallbackReason === "disabled" || (!fallbackReason && warnings.includes("ai_disabled"));
  return {
    label: "Kural tabanlı",
    text: `${reason ? `${reason} ` : ""}Mesaj kural tabanlı ayrıştırıcıyla okundu${disabled ? "" : " (yedek)"}; satırları dikkatle kontrol edin.`,
    tone: disabled || !reason ? "info" : "warning",
  };
}

export const matchStatusLabels: Record<string, string> = {
  matched: "Eşleşti",
  ambiguous: "Belirsiz",
  unmatched: "Eşleşmedi",
};

export const matchMethodLabels: Record<string, string> = {
  sku: "SKU",
  barcode: "Barkod",
  name: "Ürün adı",
  similarity: "Benzerlik",
};

export const parseStatusLabels: Record<string, string> = {
  needs_review: "İnceleme bekliyor",
  approved: "Taslak oluşturuldu",
  rejected: "Reddedildi",
};

/** Message units (ParseUnits, ASCII codes) → Turkish. */
export const unitLabels: Record<string, string> = {
  adet: "adet",
  koli: "koli",
  kutu: "kutu",
  paket: "paket",
  takim: "takım",
  set: "set",
  metre: "metre",
  rulo: "rulo",
  cift: "çift",
  kg: "kg",
  litre: "litre",
};

export function unitLabel(unit: string | null | undefined): string | null {
  if (!unit) return null;
  return unitLabels[unit] ?? unit;
}

/** 0–1 confidence as "%87" (Turkish percent). */
export function confidenceText(value: number | string | null | undefined): string {
  const n = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(n)) return "—";
  return `%${Math.round(Math.max(0, Math.min(1, n)) * 100)}`;
}
