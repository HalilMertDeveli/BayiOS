import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { SignUpForm } from "@/components/auth/sign-up-form";

export const metadata: Metadata = { title: "Kayıt ol" };

export default function SignUpPage() {
  return (
    <AuthCard title="Ücretsiz başlayın" description="Hesabınızı oluşturun, ardından işletmenizi birkaç adımda kuralım.">
      <SignUpForm />
    </AuthCard>
  );
}
