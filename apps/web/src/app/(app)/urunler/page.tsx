import { Suspense } from "react";
import { ProductList } from "@/components/catalog/product-list";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Ürünler" };

export default function UrunlerPage() {
  return (
    // The list keeps its search/filter state in the URL (useSearchParams), which is only known in the browser.
    <Suspense fallback={<PageHeader title="Ürünler" />}>
      <ProductList />
    </Suspense>
  );
}
