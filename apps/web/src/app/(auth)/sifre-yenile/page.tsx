import type { Metadata } from "next";
import { Lock } from "@/components/ui/icons";
import { AuthCard } from "@/components/auth/auth-layout";
import { UpdatePasswordForm } from "@/components/auth/update-password-form";

export const metadata: Metadata = { title: "Yeni şifre" };

export default function UpdatePasswordPage() {
  return (
    <AuthCard icon={<Lock className="size-7" />} title="Yeni şifre belirleyin">
      <UpdatePasswordForm />
    </AuthCard>
  );
}
