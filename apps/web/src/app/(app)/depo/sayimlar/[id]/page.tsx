import { Suspense } from "react";
import { CountDetailPage } from "@/components/stock-documents/count-detail";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Sayım" };

/** /depo/sayimlar/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function SayimPage({ params }: PageProps<"/depo/sayimlar/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Sayım" />}>
      {params.then(({ id }) => (
        <CountDetailPage key={id} countId={id} />
      ))}
    </Suspense>
  );
}
