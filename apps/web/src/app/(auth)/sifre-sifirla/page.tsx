import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { ResetPasswordForm } from "@/components/auth/reset-password-form";

export const metadata: Metadata = { title: "Şifre sıfırlama" };

export default function ResetPasswordPage() {
  return (
    <AuthCard title="Şifrenizi mi unuttunuz?" description="E-posta adresinizi girin, size bir sıfırlama bağlantısı gönderelim.">
      <ResetPasswordForm />
    </AuthCard>
  );
}
