import type { Metadata } from "next";
import { AuthCard } from "@/components/auth/auth-layout";
import { AcceptInvitation } from "@/components/members/accept-invitation";

export const metadata: Metadata = { title: "Davet", referrer: "no-referrer" };

export default function InvitationPage() {
  return (
    <AuthCard title="Ekibe davet edildiniz" description="BayiOS'ta bir işletmenin ekibine katılmak için daveti kabul edin.">
      <AcceptInvitation />
    </AuthCard>
  );
}
