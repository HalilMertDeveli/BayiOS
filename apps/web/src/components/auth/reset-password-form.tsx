"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authErrorMessage } from "@/lib/auth/auth-errors";
import { ROUTES } from "@/lib/auth/routes";
import { validateEmail } from "@/lib/auth/validation";
import { getSupabaseBrowserClient } from "@/lib/supabase/client";
import { FormMessage } from "./auth-layout";
import { CaptchaField, useCaptcha } from "./captcha-field";

/** Requests a password-reset e-mail. The answer never reveals whether the address has an account. */
export function ResetPasswordForm() {
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const captcha = useCaptcha("password-reset");

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fieldError = validateEmail(email);
    setError(fieldError);
    setFormError(null);
    if (fieldError) return;
    const captchaToken = captcha.tokenForSubmit();
    if (captchaToken === null) return;

    setSubmitting(true);
    const { error: authError } = await getSupabaseBrowserClient().auth.resetPasswordForEmail(email.trim(), {
      redirectTo: `${window.location.origin}${ROUTES.authCallback}?next=${encodeURIComponent(ROUTES.updatePassword)}`,
      captchaToken,
    });
    captcha.reset();
    setSubmitting(false);
    // Rate limits and network errors are worth showing; "user not found" is never reported by Supabase.
    if (authError) {
      setFormError(authErrorMessage(authError));
      return;
    }
    setSent(true);
  }

  if (sent) {
    return (
      <div className="flex flex-col gap-4">
        <FormMessage tone="success">
          Bu adrese kayıtlı bir hesap varsa şifre sıfırlama bağlantısı gönderildi. Lütfen gelen kutunuzu kontrol edin.
        </FormMessage>
        <Link href={ROUTES.signIn} className="text-body font-medium text-brand-fg underline-offset-2 hover:underline">
          Giriş sayfasına dön
        </Link>
      </div>
    );
  }

  return (
    <form noValidate onSubmit={onSubmit} className="flex flex-col gap-4" aria-label="Şifre sıfırlama formu">
      {formError ? <FormMessage tone="error">{formError}</FormMessage> : null}
      <Input
        label="E-posta"
        type="email"
        name="email"
        autoComplete="email"
        required
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        error={error}
      />
      {captcha.fieldProps ? <CaptchaField {...captcha.fieldProps} /> : null}
      <Button type="submit" size="lg" loading={submitting}>
        Sıfırlama bağlantısı gönder
      </Button>
      <Link href={ROUTES.signIn} className="text-center text-body text-brand-fg underline-offset-2 hover:underline">
        Giriş sayfasına dön
      </Link>
    </form>
  );
}
