import { Suspense } from "react";
import { WarehouseAdmin } from "@/components/inventory/warehouse-admin";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Depo" };

export default function DepoPage() {
  return (
    // The selected warehouse lives in the URL (?depo=), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Depo" />}>
      <WarehouseAdmin />
    </Suspense>
  );
}
