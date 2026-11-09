"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authErrorMessage } from "@/lib/auth/auth-errors";
import { ROUTES, safeNextPath, signInUrl } from "@/lib/auth/routes";
import {
  PASSWORD_MIN_LENGTH,
  compactErrors,
  validateEmail,
  validateFullName,
  validateNewPassword,
  validatePasswordConfirmation,
} from "@/lib/auth/validation";
import { getSupabaseBrowserClient } from "@/lib/supabase/client";
import { FormMessage } from "./auth-layout";
import { CaptchaField, useCaptcha } from "./captcha-field";

type Fields = { fullName: string; email: string; password: string; confirm: string };

export function SignUpForm() {
  const router = useRouter();
  // ?next= (same-origin only): e.g. /davet, so an invitee comes back to accept the invitation; default: onboarding.
  const next = safeNextPath(useSearchParams().get("next"), ROUTES.onboarding);
  const forInvitation = next === ROUTES.invitation;
  const [values, setValues] = useState<Fields>({ fullName: "", email: "", password: "", confirm: "" });
  const [errors, setErrors] = useState<Partial<Record<keyof Fields, string>>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [sentTo, setSentTo] = useState<string | null>(null);
  const captcha = useCaptcha("signup");

  const set = (field: keyof Fields) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setValues((v) => ({ ...v, [field]: event.target.value }));

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fieldErrors = compactErrors({
      fullName: validateFullName(values.fullName),
      email: validateEmail(values.email),
      password: validateNewPassword(values.password),
      confirm: validatePasswordConfirmation(values.password, values.confirm),
    });
    setErrors(fieldErrors);
    setFormError(null);
    if (Object.keys(fieldErrors).length > 0) return;
    const captchaToken = captcha.tokenForSubmit();
    if (captchaToken === null) return;

    setSubmitting(true);
    const email = values.email.trim();
    const { data, error } = await getSupabaseBrowserClient().auth.signUp({
      email,
      password: values.password,
      options: {
        data: { full_name: values.fullName.trim() },
        // The confirmation link returns through /auth/callback, which opens `next` (the onboarding wizard by default).
        emailRedirectTo: `${window.location.origin}${ROUTES.authCallback}?next=${encodeURIComponent(next)}`,
        captchaToken,
      },
    });
    captcha.reset();
    setSubmitting(false);
    if (error) {
      setFormError(authErrorMessage(error));
      return;
    }
    if (data.session) {
      // E-mail confirmation disabled for this project: signed in right away.
      router.replace(next);
      return;
    }
    // Same answer whether or not the address already had an account (no account enumeration).
    setSentTo(email);
  }

  if (sentTo) {
    return (
      <div className="flex flex-col gap-4">
        <FormMessage tone="success">
          <strong className="font-semibold">{sentTo}</strong> adresine bir doğrulama bağlantısı gönderdik. Bağlantıya
          tıkladıktan sonra {forInvitation ? "daveti kabul edebilirsiniz. Bağlantıyı bu tarayıcıda açın." : "işletmenizi oluşturabilirsiniz."}
        </FormMessage>
        <p className="text-body text-fg-muted">
          E-posta gelmediyse istenmeyen klasörünü kontrol edin. Adres doğrulandıysa{" "}
          <Link href={next === ROUTES.onboarding ? ROUTES.signIn : signInUrl(next)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
            giriş yapın
          </Link>
          .
        </p>
      </div>
    );
  }

  return (
    <form noValidate onSubmit={onSubmit} className="flex flex-col gap-4" aria-label="Kayıt formu">
      {formError ? <FormMessage tone="error">{formError}</FormMessage> : null}
      <Input label="Ad soyad" name="fullName" autoComplete="name" required value={values.fullName} onChange={set("fullName")} error={errors.fullName} />
      <Input label="E-posta" type="email" name="email" autoComplete="email" required value={values.email} onChange={set("email")} error={errors.email} />
      <Input
        label="Şifre"
        type="password"
        name="password"
        autoComplete="new-password"
        required
        value={values.password}
        onChange={set("password")}
        error={errors.password}
        hint={`En az ${PASSWORD_MIN_LENGTH} karakter; harf ve rakam içermeli.`}
      />
      <Input
        label="Şifre (tekrar)"
        type="password"
        name="confirm"
        autoComplete="new-password"
        required
        value={values.confirm}
        onChange={set("confirm")}
        error={errors.confirm}
      />
      {captcha.fieldProps ? <CaptchaField {...captcha.fieldProps} /> : null}
      <Button type="submit" size="lg" loading={submitting}>
        Hesap oluştur
      </Button>
      <p className="text-center text-body text-fg-muted">
        Zaten hesabınız var mı?{" "}
        <Link href={next === ROUTES.onboarding ? ROUTES.signIn : signInUrl(next)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
          Giriş yapın
        </Link>
      </p>
    </form>
  );
}
