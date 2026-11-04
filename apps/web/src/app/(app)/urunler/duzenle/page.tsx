import { Suspense } from "react";
import { ProductEditorRoute } from "@/components/catalog/product-editor-route";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Ürün" };

export default function UrunDuzenlePage() {
  return (
    // The product id is a query parameter (useSearchParams), only known in the browser.
    <Suspense fallback={<PageHeader title="Ürün" />}>
      <ProductEditorRoute />
    </Suspense>
  );
}
