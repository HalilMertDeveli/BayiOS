import { Suspense } from "react";
import { RelocationForm } from "@/components/stock-documents/relocation-form";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Raf taşıma" };

export default function RafTasimaPage() {
  return (
    // The pre-fill (?warehouseId=&variantId=&fromLocationId=) is only known in the browser.
    <Suspense fallback={<PageHeader title="Raf taşıma" />}>
      <RelocationForm />
    </Suspense>
  );
}
