import { Suspense } from "react";
import { ShipmentDetailPage } from "@/components/fulfilment/shipment-detail";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Sevkiyat" };

/** /depo/sevkiyatlar/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function SevkiyatPage({ params }: PageProps<"/depo/sevkiyatlar/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Sevkiyat" />}>
      {params.then(({ id }) => (
        <ShipmentDetailPage key={id} shipmentId={id} />
      ))}
    </Suspense>
  );
}
