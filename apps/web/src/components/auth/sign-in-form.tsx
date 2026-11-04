"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authErrorMessage } from "@/lib/auth/auth-errors";
import { ROUTES, safeNextPath } from "@/lib/auth/routes";
import { compactErrors, validateEmail, validatePasswordPresent } from "@/lib/auth/validation";
import { getSupabaseBrowserClient } from "@/lib/supabase/client";
import { FormMessage } from "./auth-layout";

const NOTICES: Record<string, { tone: "error" | "success"; text: string }> = {
  "baglanti-gecersiz": {
    tone: "error",
    text: "Bağlantı geçersiz veya süresi dolmuş. E-postanızı doğruladıysanız giriş yapabilirsiniz; aksi halde yeni bir bağlantı isteyin.",
  },
  "sifre-guncellendi": { tone: "success", text: "Şifreniz güncellendi. Yeni şifrenizle giriş yapabilirsiniz." },
};

export function SignInForm() {
  const router = useRouter();
  const params = useSearchParams();
  const next = safeNextPath(params.get("next"));
  const notice = NOTICES[params.get("durum") ?? ""];

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [errors, setErrors] = useState<{ email?: string; password?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fieldErrors = compactErrors({ email: validateEmail(email), password: validatePasswordPresent(password) });
    setErrors(fieldErrors);
    setFormError(null);
    if (Object.keys(fieldErrors).length > 0) return;

    setSubmitting(true);
    const { error } = await getSupabaseBrowserClient().auth.signInWithPassword({ email: email.trim(), password });
    if (error) {
      setFormError(authErrorMessage(error));
      setSubmitting(false);
      return;
    }
    router.replace(next);
  }

  return (
    <form noValidate onSubmit={onSubmit} className="flex flex-col gap-4" aria-label="Giriş formu">
      {notice ? <FormMessage tone={notice.tone}>{notice.text}</FormMessage> : null}
      {formError ? <FormMessage tone="error">{formError}</FormMessage> : null}
      <Input
        label="E-posta"
        type="email"
        name="email"
        autoComplete="email"
        required
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        error={errors.email}
      />
      <Input
        label="Şifre"
        type="password"
        name="password"
        autoComplete="current-password"
        required
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        error={errors.password}
      />
      <div className="flex justify-end">
        <Link href={ROUTES.resetPassword} className="text-body text-brand-fg underline-offset-2 hover:underline">
          Şifremi unuttum
        </Link>
      </div>
      <Button type="submit" size="lg" loading={submitting}>
        Giriş yap
      </Button>
      <p className="text-center text-body text-fg-muted">
        Hesabınız yok mu?{" "}
        <Link href={ROUTES.signUp} className="font-medium text-brand-fg underline-offset-2 hover:underline">
          Ücretsiz kayıt olun
        </Link>
      </p>
    </form>
  );
}
