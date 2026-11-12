import type { Metadata } from "next";
import { Building2 } from "@/components/ui/icons";
import { AuthCard } from "@/components/auth/auth-layout";
import { TenantPicker } from "@/components/onboarding/tenant-picker";

export const metadata: Metadata = { title: "İşletme seç" };

export default function TenantPickerPage() {
  return (
    <AuthCard icon={<Building2 className="size-7" />} title="İşletme seçin" description="Devam etmek istediğiniz işletmeyi seçin.">
      <TenantPicker />
    </AuthCard>
  );
}
