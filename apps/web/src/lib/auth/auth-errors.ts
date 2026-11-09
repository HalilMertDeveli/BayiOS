/** Supabase Auth error → Turkish message. Codes: https://supabase.com/docs/guides/auth/debugging/error-codes */
type AuthErrorLike = { code?: string | null; status?: number | null; name?: string; message?: string } | null | undefined;

const MESSAGES: Record<string, string> = {
  invalid_credentials: "E-posta adresi veya şifre hatalı.",
  email_not_confirmed:
    "E-posta adresiniz henüz doğrulanmadı. Gelen kutunuzdaki doğrulama bağlantısına tıklayıp tekrar deneyin.",
  user_already_exists: "Bu e-posta adresiyle zaten bir hesap var. Giriş yapmayı veya şifre sıfırlamayı deneyin.",
  email_exists: "Bu e-posta adresiyle zaten bir hesap var. Giriş yapmayı veya şifre sıfırlamayı deneyin.",
  weak_password: "Şifre yeterince güçlü değil. Daha uzun ve tahmin edilmesi zor bir şifre seçin.",
  same_password: "Yeni şifre eski şifrenizden farklı olmalı.",
  signup_disabled: "Şu anda yeni kayıt alınmıyor.",
  over_request_rate_limit: "Çok sayıda deneme yapıldı. Lütfen birkaç dakika bekleyip tekrar deneyin.",
  over_email_send_rate_limit: "Kısa sürede çok fazla e-posta istendi. Lütfen birkaç dakika bekleyip tekrar deneyin.",
  session_not_found: "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.",
  session_expired: "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.",
  otp_expired: "Bağlantının süresi dolmuş. Lütfen yeni bir bağlantı isteyin.",
  validation_failed: "Girilen bilgiler geçersiz. Lütfen kontrol edip tekrar deneyin.",
  captcha_failed: "Güvenlik doğrulaması başarısız oldu. Doğrulamayı yeniden tamamlayıp tekrar deneyin.",
};

export function authErrorMessage(error: AuthErrorLike): string {
  if (error?.code && MESSAGES[error.code]) return MESSAGES[error.code];
  if (error?.name === "AuthRetryableFetchError" || error?.status === 0) {
    return "Kimlik doğrulama sunucusuna ulaşılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.";
  }
  if (error?.status === 429) return MESSAGES.over_request_rate_limit;
  return "İşlem tamamlanamadı. Lütfen tekrar deneyin.";
}
