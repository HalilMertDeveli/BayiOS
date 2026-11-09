import { Suspense } from "react";
import { TransferList } from "@/components/stock-documents/transfer-list";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Transferler" };

export default function TransferlerPage() {
  return (
    // Filters live in the URL (?durum=&depo=), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Transferler" />}>
      <TransferList />
    </Suspense>
  );
}
