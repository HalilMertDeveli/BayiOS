/** Client-side form checks (UX only — Supabase Auth and the API validate again). Messages are Turkish. */

export const PASSWORD_MIN_LENGTH = 8;

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function validateEmail(value: string): string | undefined {
  const email = value.trim();
  if (!email) return "E-posta adresinizi girin.";
  if (!EMAIL_PATTERN.test(email)) return "Geçerli bir e-posta adresi girin (ör. ad@firma.com).";
  return undefined;
}

export function validatePasswordPresent(value: string): string | undefined {
  return value ? undefined : "Şifrenizi girin.";
}

export function validateNewPassword(value: string): string | undefined {
  if (!value) return "Bir şifre belirleyin.";
  if (value.length < PASSWORD_MIN_LENGTH) return `Şifre en az ${PASSWORD_MIN_LENGTH} karakter olmalı.`;
  if (!/[A-Za-zÇĞİÖŞÜçğıöşü]/.test(value) || !/\d/.test(value)) return "Şifre en az bir harf ve bir rakam içermeli.";
  return undefined;
}

export function validatePasswordConfirmation(password: string, confirmation: string): string | undefined {
  if (!confirmation) return "Şifrenizi tekrar girin.";
  return password === confirmation ? undefined : "Şifreler eşleşmiyor.";
}

export function validateFullName(value: string): string | undefined {
  const name = value.trim();
  if (!name) return "Adınızı ve soyadınızı girin.";
  if (name.length < 2) return "Ad soyad en az 2 karakter olmalı.";
  if (name.length > 120) return "Ad soyad en fazla 120 karakter olabilir.";
  return undefined;
}

/** Field errors object without the undefined entries; empty object = valid. */
export function compactErrors<T extends Record<string, string | undefined>>(errors: T): Partial<Record<keyof T, string>> {
  return Object.fromEntries(Object.entries(errors).filter(([, v]) => v !== undefined)) as Partial<Record<keyof T, string>>;
}
