import { Suspense } from "react";
import { OrdersReportView } from "@/components/reports/orders-report";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Sipariş durumları" };

export default function SiparisRaporuPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Sipariş durumları" />}>
      <OrdersReportView />
    </Suspense>
  );
}
