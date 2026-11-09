import { Suspense } from "react";
import { TransferDetailPage } from "@/components/stock-documents/transfer-detail";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Transfer" };

/** /depo/transferler/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function TransferPage({ params }: PageProps<"/depo/transferler/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Transfer" />}>
      {params.then(({ id }) => (
        <TransferDetailPage key={id} transferId={id} />
      ))}
    </Suspense>
  );
}
