import { Suspense } from "react";
import { OrderDetailPage } from "@/components/orders/order-detail";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Sipariş" };

/** /siparisler/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function SiparisPage({ params }: PageProps<"/siparisler/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Sipariş" />}>
      {params.then(({ id }) => (
        <OrderDetailPage key={id} orderId={id} />
      ))}
    </Suspense>
  );
}
