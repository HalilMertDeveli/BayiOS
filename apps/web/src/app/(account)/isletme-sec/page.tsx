import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { TenantPicker } from "@/components/onboarding/tenant-picker";

export const metadata: Metadata = { title: "İşletme seç" };

export default function TenantPickerPage() {
  return (
    <AuthCard title="İşletme seçin" description="Devam etmek istediğiniz işletmeyi seçin.">
      <TenantPicker />
    </AuthCard>
  );
}
