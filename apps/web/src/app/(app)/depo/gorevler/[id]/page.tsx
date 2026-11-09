import { Suspense } from "react";
import { PickScreen } from "@/components/fulfilment/pick-screen";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Toplama" };

/** /depo/gorevler/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function GorevPage({ params }: PageProps<"/depo/gorevler/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Toplama görevi" />}>
      {params.then(({ id }) => (
        <PickScreen key={id} taskId={id} />
      ))}
    </Suspense>
  );
}
