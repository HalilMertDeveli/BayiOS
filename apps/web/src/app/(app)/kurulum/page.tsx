import { Suspense } from "react";
import { SetupWizard } from "@/components/onboarding/setup-wizard";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Kurulum" };

/** /kurulum (#83): guided setup after the business is created. The step lives in the URL (?adim=), read in the browser. */
export default function KurulumPage() {
  return (
    <Suspense fallback={<PageHeader title="İşletmenizi kuralım" />}>
      <SetupWizard />
    </Suspense>
  );
}
