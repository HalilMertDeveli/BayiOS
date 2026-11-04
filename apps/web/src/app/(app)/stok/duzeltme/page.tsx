import { Suspense } from "react";
import { AdjustmentForm } from "@/components/inventory/adjustment-form";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Stok düzeltme" };

export default function StokDuzeltmePage() {
  return (
    // Optional pre-fill (?variantId=&warehouseId=&locationId=) is request data.
    <Suspense fallback={<PageHeader title="Stok düzeltme" />}>
      <AdjustmentForm />
    </Suspense>
  );
}
