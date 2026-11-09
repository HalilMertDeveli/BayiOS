import { describe, expect, it } from "vitest";
import { authErrorMessage } from "./auth-errors";
import { compactErrors, validateEmail, validateNewPassword, validatePasswordConfirmation } from "./validation";

describe("form validation", () => {
  it("checks e-mail addresses", () => {
    expect(validateEmail("")).toBe("E-posta adresinizi girin.");
    expect(validateEmail("ad@firma")).toContain("Geçerli bir e-posta");
    expect(validateEmail(" ad@firma.com ")).toBeUndefined();
  });

  it("requires 8+ characters with a letter and a digit", () => {
    expect(validateNewPassword("")).toBe("Bir şifre belirleyin.");
    expect(validateNewPassword("Abc1")).toContain("en az 8");
    expect(validateNewPassword("abcdefgh")).toContain("rakam");
    expect(validateNewPassword("şifre1234")).toBeUndefined();
  });

  it("compares the confirmation", () => {
    expect(validatePasswordConfirmation("a", "")).toBe("Şifrenizi tekrar girin.");
    expect(validatePasswordConfirmation("a", "b")).toBe("Şifreler eşleşmiyor.");
    expect(validatePasswordConfirmation("a", "a")).toBeUndefined();
  });

  it("drops undefined entries", () => {
    expect(compactErrors({ a: undefined, b: "x" })).toEqual({ b: "x" });
  });
});

describe("authErrorMessage", () => {
  it("maps Supabase error codes to Turkish", () => {
    expect(authErrorMessage({ code: "invalid_credentials" })).toBe("E-posta adresi veya şifre hatalı.");
    expect(authErrorMessage({ code: "email_not_confirmed" })).toContain("doğrulanmadı");
    expect(authErrorMessage({ name: "AuthRetryableFetchError", status: 0 })).toContain("ulaşılamadı");
    expect(authErrorMessage({ status: 429 })).toContain("Çok sayıda deneme");
    expect(authErrorMessage({ code: "something_new", message: "English" })).toBe("İşlem tamamlanamadı. Lütfen tekrar deneyin.");
  });
});
