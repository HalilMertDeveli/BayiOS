"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authErrorMessage } from "@/lib/auth/auth-errors";
import { ROUTES } from "@/lib/auth/routes";
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

type Fields = { fullName: string; email: string; password: string; confirm: string };

export function SignUpForm() {
  const router = useRouter();
  const [values, setValues] = useState<Fields>({ fullName: "", email: "", password: "", confirm: "" });
  const [errors, setErrors] = useState<Partial<Record<keyof Fields, string>>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [sentTo, setSentTo] = useState<string | null>(null);

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

    setSubmitting(true);
    const email = values.email.trim();
    const { data, error } = await getSupabaseBrowserClient().auth.signUp({
      email,
      password: values.password,
      options: {
        data: { full_name: values.fullName.trim() },
        // The confirmation link returns through /auth/callback, which opens the onboarding wizard.
        emailRedirectTo: `${window.location.origin}${ROUTES.authCallback}?next=${encodeURIComponent(ROUTES.onboarding)}`,
      },
    });
    setSubmitting(false);
    if (error) {
      setFormError(authErrorMessage(error));
      return;
    }
    if (data.session) {
      // E-mail confirmation disabled for this project: signed in right away.
      router.replace(ROUTES.onboarding);
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
          tıkladıktan sonra işletmenizi oluşturabilirsiniz.
        </FormMessage>
        <p className="text-body text-fg-muted">
          E-posta gelmediyse istenmeyen klasörünü kontrol edin. Adres doğrulandıysa{" "}
          <Link href={ROUTES.signIn} className="font-medium text-brand-fg underline-offset-2 hover:underline">
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
      <Button type="submit" size="lg" loading={submitting}>
        Hesap oluştur
      </Button>
      <p className="text-center text-body text-fg-muted">
        Zaten hesabınız var mı?{" "}
        <Link href={ROUTES.signIn} className="font-medium text-brand-fg underline-offset-2 hover:underline">
          Giriş yapın
        </Link>
      </p>
    </form>
  );
}
