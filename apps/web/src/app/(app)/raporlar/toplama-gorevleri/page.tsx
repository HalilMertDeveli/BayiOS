import { Suspense } from "react";
import { PickingTasksReportView } from "@/components/reports/picking-tasks-report";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Toplama görevleri" };

export default function ToplamaGorevleriPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Toplama görevleri" />}>
      <PickingTasksReportView />
    </Suspense>
  );
}
