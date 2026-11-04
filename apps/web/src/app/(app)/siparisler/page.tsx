import { Suspense } from "react";
import { OrderList } from "@/components/orders/order-list";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Siparişler" };

export default function SiparislerPage() {
  return (
    // Search and filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Siparişler" />}>
      <OrderList />
    </Suspense>
  );
}
