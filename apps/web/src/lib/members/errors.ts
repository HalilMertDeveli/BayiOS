import { ApiError } from "@/lib/api/client";
import { entitlementMessage } from "@/lib/api/problems";
import { roleLabel } from "./labels";

const GRANT_RULE = "Yöneticiler yalnızca operasyon müdürü ve altındaki rolleri verebilir; yönetici rolünü yalnızca işletme sahibi verir.";

/** Team management problem codes (#129, api-strategy §6) → Turkish. */
export const MEMBER_MESSAGES: Record<string, string> = {
  "members.role_not_grantable": `Bu rolü veremezsiniz. ${GRANT_RULE}`,
  "members.not_manageable": "Bu kişiyi yönetemezsiniz. Yöneticiler işletme sahibini ve diğer yöneticileri değiştiremez.",
  "members.last_owner": "İşletmenin en az bir aktif sahibi olmalı. Önce sahipliği başka bir üyeye devredin.",
  "members.cannot_disable_self": "Kendi üyeliğinizi askıya alamazsınız.",
  "members.already_member": "Bu e-posta adresi zaten işletmenizin bir üyesine ait.",
  "members.transfer_to_self": "Bu işletmenin sahibi zaten sizsiniz.",
  "members.not_active": "Sahiplik yalnızca aktif bir üyeye devredilebilir.",
  "members.already_owner": "Bu kişi zaten işletme sahibi.",
  "invitation.already_pending": "Bu e-posta adresine gönderilmiş, bekleyen bir davet zaten var. Yeni bağlantı oluşturabilir veya daveti iptal edebilirsiniz.",
  "invitation.not_pending": "Bu davet artık beklemede değil (kabul edilmiş veya iptal edilmiş). Liste güncellendi.",
  "invitation.not_found": "Davet bulunamadı. Liste güncellendi.",
};

const FALLBACK = "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";

/** Message for a failed team-management call (invite, resend, revoke, role, disable/enable, transfer). */
export function memberErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return FALLBACK;
  if (error.code === "entitlement.limit_exceeded") {
    return entitlementMessage(error.problem) ?? "Paketinizdeki kullanıcı sınırına ulaştınız. Bekleyen davetler de koltuk sayılır.";
  }
  if (error.code === "members.role_not_grantable") {
    const role = typeof error.problem?.role === "string" ? error.problem.role : null;
    return role ? `“${roleLabel(role)}” rolünü veremezsiniz. ${GRANT_RULE}` : MEMBER_MESSAGES[error.code];
  }
  if (error.code && MEMBER_MESSAGES[error.code]) return MEMBER_MESSAGES[error.code];
  if (error.status === 404) return "Üye bulunamadı. Liste güncellendi.";
  return error.userMessage;
}

/** invitationId of a 409 invitation.already_pending (to offer "new link" for that invitation). */
export function pendingInvitationId(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.code !== "invitation.already_pending") return null;
  const id = error.problem?.invitationId;
  return typeof id === "string" && id ? id : null;
}

/** A 404/409 after which the list is stale and should be reloaded. */
export function isStaleMemberState(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 404 || error.code === "invitation.not_pending");
}

export type AcceptFailure = {
  title: string;
  message: string;
  /**
   * What the page offers next: "switch-account" (sign out, sign in with the invited address), "verify" (confirm the
   * e-mail, then retry), "retry" (transient), "go-app" (already a member), "none" (ask for a new link).
   */
  next: "switch-account" | "verify" | "retry" | "go-app" | "none";
  /** True when the token can never work again: it is removed from this tab. */
  final: boolean;
};

/** POST /invitations/accept errors → what the invitee reads (threat-model §4.1). */
export function acceptFailure(error: unknown): AcceptFailure {
  if (!(error instanceof ApiError)) {
    return { title: "Davet kabul edilemedi", message: FALLBACK, next: "retry", final: false };
  }
  switch (error.code) {
    case "invitation.email_mismatch":
      return {
        title: "Bu davet başka bir e-posta adresi için",
        message: "Davet, giriş yaptığınız hesaptan farklı bir e-posta adresine gönderilmiş. Çıkış yapıp davet edilen adresle giriş yapın veya o adresle yeni hesap oluşturun.",
        next: "switch-account",
        final: false,
      };
    case "invitation.email_not_verified":
      return {
        title: "E-posta adresiniz doğrulanmamış",
        message: "Daveti kabul etmeden önce e-posta adresinizi doğrulayın: size gönderilen doğrulama bağlantısına tıklayın, sonra bu sayfaya dönüp tekrar deneyin.",
        next: "verify",
        final: false,
      };
    case "invitation.expired":
      return {
        title: "Davetin süresi dolmuş",
        message: "Davet bağlantıları 7 gün geçerlidir. Sizi davet eden kişiden yeni bir bağlantı isteyin.",
        next: "none",
        final: true,
      };
    case "invitation.not_found":
      return {
        title: "Davet geçersiz",
        message: "Bu bağlantı artık geçerli değil: kullanılmış, iptal edilmiş ya da yerine yeni bir bağlantı oluşturulmuş olabilir. Sizi davet eden kişiden yeni bir bağlantı isteyin.",
        next: "none",
        final: true,
      };
    case "invitation.already_member":
      return {
        title: "Zaten bu işletmenin üyesisiniz",
        message: "Bu işletmede zaten bir üyeliğiniz var; davetin kabul edilmesine gerek yok. Üyeliğiniz askıdaysa işletme yöneticinizle görüşün.",
        next: "go-app",
        final: true,
      };
    case "tenant.inactive":
      return {
        title: "İşletme kullanılamıyor",
        message: "Bu işletme askıya alınmış veya kapatılmış; davet şu anda kabul edilemiyor. Sizi davet eden kişiyle görüşün.",
        next: "none",
        final: false,
      };
    case "entitlement.limit_exceeded":
      return {
        title: "İşletmenin kullanıcı sınırı dolu",
        message: "Davet eden işletmenin paketindeki kullanıcı sınırına ulaşılmış. Sizi davet eden kişiden yer açmasını isteyin, sonra tekrar deneyin.",
        next: "retry",
        final: false,
      };
  }
  if (error.status === 401) {
    return { title: "Oturumunuz sona erdi", message: "Lütfen yeniden giriş yapın; davetiniz bu sekmede saklanıyor.", next: "switch-account", final: false };
  }
  return { title: "Davet kabul edilemedi", message: error.userMessage, next: "retry", final: false };
}
