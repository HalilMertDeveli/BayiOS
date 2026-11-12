import { Suspense } from "react";
import { UserPlus } from "@/components/ui/icons";
import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { SignUpForm } from "@/components/auth/sign-up-form";

export const metadata: Metadata = { title: "Kayıt ol" };

export default function SignUpPage() {
  return (
    <AuthCard icon={<UserPlus className="size-7" />} title="Ücretsiz başlayın" description="Hesabınızı oluşturun, ardından işletmenizi birkaç adımda kuralım.">
      {/* useSearchParams (?next=, e.g. back to an invitation) is request data: it streams in behind this boundary. */}
      <Suspense fallback={<p className="text-body text-fg-muted">Yükleniyor…</p>}>
        <SignUpForm />
      </Suspense>
    </AuthCard>
  );
}
