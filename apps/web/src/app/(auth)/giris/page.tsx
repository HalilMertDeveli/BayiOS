import { Suspense } from "react";
import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { SignInForm } from "@/components/auth/sign-in-form";

export const metadata: Metadata = { title: "Giriş yap" };

export default function SignInPage() {
  return (
    <AuthCard title="Giriş yap" description="BayiOS hesabınızla devam edin.">
      {/* useSearchParams (?next=) is request data: it streams in behind this boundary. */}
      <Suspense fallback={<p className="text-body text-fg-muted">Yükleniyor…</p>}>
        <SignInForm />
      </Suspense>
    </AuthCard>
  );
}
