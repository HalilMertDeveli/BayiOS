import { Suspense } from "react";
import { ImportWizard } from "@/components/imports/import-wizard";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "İçe aktarma" };

/** /ice-aktarma/[id]: request-time param read behind Suspense (Cache Components, no generateStaticParams). */
export default function IceAktarmaDetayPage({ params }: PageProps<"/ice-aktarma/[id]">) {
  return (
    <Suspense fallback={<PageHeader title="İçe aktarma" />}>
      {params.then(({ id }) => (
        <ImportWizard key={id} id={id} />
      ))}
    </Suspense>
  );
}
