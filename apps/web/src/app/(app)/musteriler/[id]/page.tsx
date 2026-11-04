import { Suspense } from "react";
import { CustomerEditor } from "@/components/customers/customer-editor";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Müşteri" };

/** /musteriler/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function MusteriPage({ params }: PageProps<"/musteriler/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="Müşteri" />}>
      {params.then(({ id }) => (
        <CustomerEditor key={id} customerId={id} />
      ))}
    </Suspense>
  );
}
