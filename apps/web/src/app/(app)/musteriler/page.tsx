import { Suspense } from "react";
import { CustomerList } from "@/components/customers/customer-list";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Müşteriler" };

export default function MusterilerPage() {
  return (
    // Search and filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Müşteriler" />}>
      <CustomerList />
    </Suspense>
  );
}
