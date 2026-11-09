import { Suspense } from "react";
import { AiUsageReportView } from "@/components/reports/ai-usage-report";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "AI asistan kullanımı" };

export default function AiKullanimiPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="AI asistan kullanımı" />}>
      <AiUsageReportView />
    </Suspense>
  );
}
