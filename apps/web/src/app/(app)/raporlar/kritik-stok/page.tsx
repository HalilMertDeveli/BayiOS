import { Suspense } from "react";
import { CriticalStockReportView } from "@/components/reports/critical-stock-report";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Kritik stok" };

export default function KritikStokPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Kritik stok" />}>
      <CriticalStockReportView />
    </Suspense>
  );
}
