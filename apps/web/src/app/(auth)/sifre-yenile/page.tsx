import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { UpdatePasswordForm } from "@/components/auth/update-password-form";

export const metadata: Metadata = { title: "Yeni şifre" };

export default function UpdatePasswordPage() {
  return (
    <AuthCard title="Yeni şifre belirleyin">
      <UpdatePasswordForm />
    </AuthCard>
  );
}
