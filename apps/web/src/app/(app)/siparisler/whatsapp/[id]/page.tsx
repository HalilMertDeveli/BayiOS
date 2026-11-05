import { Suspense } from "react";
import { OrderParseReviewPage } from "@/components/assistant/order-parse-review";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "WhatsApp siparişini incele" };

/** /siparisler/whatsapp/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function WhatsappIncelemePage({ params }: PageProps<"/siparisler/whatsapp/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="WhatsApp siparişini incele" />}>
      {params.then(({ id }) => (
        <OrderParseReviewPage key={id} parseId={id} />
      ))}
    </Suspense>
  );
}
