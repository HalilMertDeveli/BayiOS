import { Suspense } from "react";
import { CountList } from "@/components/stock-documents/count-list";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Sayımlar" };

export default function SayimlarPage() {
  return (
    // Filters live in the URL (?durum=&depo=), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Sayımlar" />}>
      <CountList />
    </Suspense>
  );
}
