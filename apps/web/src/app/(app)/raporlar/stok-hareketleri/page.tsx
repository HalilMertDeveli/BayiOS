import { Suspense } from "react";
import { StockMovementsReportView } from "@/components/reports/stock-movements-report";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Stok hareketleri" };

export default function StokHareketleriPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Stok hareketleri" />}>
      <StockMovementsReportView />
    </Suspense>
  );
}
