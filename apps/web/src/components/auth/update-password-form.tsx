"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { authErrorMessage } from "@/lib/auth/auth-errors";
import { ROUTES } from "@/lib/auth/routes";
import { PASSWORD_MIN_LENGTH, compactErrors, validateNewPassword, validatePasswordConfirmation } from "@/lib/auth/validation";
import { getSupabaseBrowserClient } from "@/lib/supabase/client";
import { FormMessage } from "./auth-layout";

/** Sets a new password for the session opened by the reset link (/auth/callback → here). */
export function UpdatePasswordForm() {
  const router = useRouter();
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [errors, setErrors] = useState<{ password?: string; confirm?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fieldErrors = compactErrors({
      password: validateNewPassword(password),
      confirm: validatePasswordConfirmation(password, confirm),
    });
    setErrors(fieldErrors);
    setFormError(null);
    if (Object.keys(fieldErrors).length > 0) return;

    setSubmitting(true);
    const { error } = await getSupabaseBrowserClient().auth.updateUser({ password });
    if (error) {
      setFormError(authErrorMessage(error));
      setSubmitting(false);
      return;
    }
    router.replace(ROUTES.home);
  }

  return (
    <form noValidate onSubmit={onSubmit} className="flex flex-col gap-4" aria-label="Yeni şifre formu">
      {formError ? <FormMessage tone="error">{formError}</FormMessage> : null}
      <Input
        label="Yeni şifre"
        type="password"
        autoComplete="new-password"
        required
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        error={errors.password}
        hint={`En az ${PASSWORD_MIN_LENGTH} karakter; harf ve rakam içermeli.`}
      />
      <Input
        label="Yeni şifre (tekrar)"
        type="password"
        autoComplete="new-password"
        required
        value={confirm}
        onChange={(e) => setConfirm(e.target.value)}
        error={errors.confirm}
      />
      <Button type="submit" size="lg" loading={submitting}>
        Şifreyi güncelle
      </Button>
    </form>
  );
}
