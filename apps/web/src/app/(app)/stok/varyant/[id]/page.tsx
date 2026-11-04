import { Suspense } from "react";
import { VariantStock } from "@/components/inventory/variant-stock";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Ürün stoğu" };

/** /stok/varyant/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function VaryantStokPage({ params }: PageProps<"/stok/varyant/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Ürün stoğu" />}>
      {params.then(({ id }) => (
        <VariantStock key={id} variantId={id} />
      ))}
    </Suspense>
  );
}
