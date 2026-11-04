"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { SearchX } from "lucide-react";
import { buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { ProductEditor } from "./product-editor";

/** /urunler/duzenle?id=<uuid>: the product page (view or edit, depending on the role). */
export function ProductEditorRoute() {
  const id = useSearchParams().get("id");
  if (!id) {
    return (
      <>
        <PageHeader title="Ürün" />
        <section className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<SearchX className="size-6" />}
            title="Ürün seçilmedi"
            description="Bağlantıda ürün bilgisi yok. Ürünü listeden açın."
            action={
              <Link href="/urunler" className={buttonClasses("secondary")}>
                Ürün listesine dön
              </Link>
            }
          />
        </section>
      </>
    );
  }
  return <ProductEditor key={id} productId={id} />;
}
