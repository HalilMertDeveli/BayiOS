import type { Metadata } from "next";
import { Lock } from "@/components/ui/icons";
import { AuthCard } from "@/components/auth/auth-layout";
import { ResetPasswordForm } from "@/components/auth/reset-password-form";

export const metadata: Metadata = { title: "Şifre sıfırlama" };

export default function ResetPasswordPage() {
  return (
    <AuthCard icon={<Lock className="size-7" />} title="Şifrenizi mi unuttunuz?" description="E-posta adresinizi girin, size bir sıfırlama bağlantısı gönderelim.">
      <ResetPasswordForm />
    </AuthCard>
  );
}
