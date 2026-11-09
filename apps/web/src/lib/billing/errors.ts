import { ApiError } from "@/lib/api/client";
import type { Access } from "@/lib/session/access";

/** 409 codes of the module store (BillingProblems) → Turkish. */
export const BILLING_MESSAGES: Record<string, string> = {
  "billing.module_unavailable": "Bu modül henüz hazır değil; etkinleştirilemez. Hazır olduğunda burada açılabilecek.",
  "billing.module_already_active": "Bu modül zaten etkin. Liste güncellendi.",
  "billing.trial_used":
    "Bu modülün ücretsiz denemesi daha önce kullanıldı. Ücretli etkinleştirme henüz yok; ödeme alınmaz, bilgi için bizimle iletişime geçin.",
  "billing.module_not_active": "Bu modül etkin değil; kapatılacak bir şey yok. Denemeye dahil modüller deneme bitince kendiliğinden kapanır.",
};

/** User-facing message for a failed billing call (by problem `code`). */
export function billingErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
  if (error.code && BILLING_MESSAGES[error.code]) return BILLING_MESSAGES[error.code];
  if (error.status === 404) return "Bu modül katalogda bulunamadı. Sayfayı yenileyin.";
  return error.userMessage;
}

/** A 409 after which the store list is stale and should be reloaded. */
export function isStaleModuleState(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409;
}

export type BillingPermissions = {
  /** billing.read — plan, usage and the module store (owner, admin). */
  read: boolean;
  /** billing.manage — activate/deactivate add-ons (owner). */
  manage: boolean;
};

/** Visibility only: the API checks both permissions again. */
export function billingPermissions(access: Access): BillingPermissions {
  return { read: access.can("billing.read"), manage: access.can("billing.manage") };
}
