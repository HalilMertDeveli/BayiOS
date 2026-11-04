import { Suspense } from "react";
import { StockBalances } from "@/components/inventory/stock-balances";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Stok" };

export default function StokPage() {
  return (
    // Filters live in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Stok" />}>
      <StockBalances />
    </Suspense>
  );
}
