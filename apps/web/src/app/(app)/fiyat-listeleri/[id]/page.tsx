import { Suspense } from "react";
import { PriceListDetail } from "@/components/pricing/price-list-detail";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Fiyat listesi" };

/** /fiyat-listeleri/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function FiyatListesiPage({ params }: PageProps<"/fiyat-listeleri/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Fiyat listesi" />}>
      {params.then(({ id }) => (
        <PriceListDetail key={id} priceListId={id} />
      ))}
    </Suspense>
  );
}
