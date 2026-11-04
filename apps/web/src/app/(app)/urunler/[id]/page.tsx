import { Suspense } from "react";
import { ProductEditor } from "@/components/catalog/product-editor";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Ürün" };

/**
 * /urunler/[id]: the product page (view or edit, depending on the role). The id is a request-time param
 * (no generateStaticParams), so it is read behind a Suspense boundary; the (app) shell has its own boundary.
 */
export default function UrunPage({ params }: PageProps<"/urunler/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Ürün" />}>
      {params.then(({ id }) => (
        <ProductEditor key={id} productId={id} />
      ))}
    </Suspense>
  );
}
