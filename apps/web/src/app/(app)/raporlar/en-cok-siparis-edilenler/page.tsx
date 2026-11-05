import { Suspense } from "react";
import { TopProductsReportView } from "@/components/reports/top-products-report";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "En çok sipariş edilen ürünler" };

export default function EnCokSiparisEdilenlerPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="En çok sipariş edilen ürünler" />}>
      <TopProductsReportView />
    </Suspense>
  );
}
