import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { Showcase } from "./showcase";

export const metadata: Metadata = { title: "Tasarım sistemi" };

/** Internal component catalogue (not in the main menu). All data on this page is fictional. */
export default function DesignSystemPage() {
  return (
    <>
      <PageHeader
        title="Tasarım sistemi"
        description="Geliştirme amaçlı bileşen kataloğu. Bu sayfadaki tüm veriler kurgusaldır."
      />
      <Showcase />
    </>
  );
}
