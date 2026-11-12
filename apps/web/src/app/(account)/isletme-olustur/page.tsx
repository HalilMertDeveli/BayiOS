import type { Metadata } from "next";
import { Building2 } from "@/components/ui/icons";
import { AuthCard } from "@/components/auth/auth-layout";
import { OnboardingWizard } from "@/components/onboarding/onboarding-wizard";

export const metadata: Metadata = { title: "İşletme oluştur" };

export default function OnboardingPage() {
  return (
    <AuthCard icon={<Building2 className="size-7" />} title="İşletmenizi oluşturun" description="Birkaç adımda işletmenizi kuralım. Bilgileri daha sonra değiştirebilirsiniz.">
      <OnboardingWizard />
    </AuthCard>
  );
}
