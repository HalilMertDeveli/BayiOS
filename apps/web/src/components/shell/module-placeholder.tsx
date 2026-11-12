import type { ReactNode } from "react";
import { Construction } from "@/components/ui/icons";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { findNavItem } from "@/lib/navigation";

/**
 * Honest placeholder for a module that is not built yet: no sample rows, no fake numbers.
 */
export function ModulePlaceholder({ href, children }: { href: string; children?: ReactNode }) {
  const item = findNavItem(href);
  return (
    <>
      <PageHeader title={item.label} />
      <section aria-label={`${item.label} durumu`} className="rounded-xl border border-line bg-card shadow-e1">
        <EmptyState
          icon={<Construction className="size-6" />}
          title="Bu modül henüz geliştiriliyor"
          description={
            <>
              <p>{item.description}</p>
              <p className="mt-1">Bu ekranda henüz gerçek veri gösterilmiyor.</p>
            </>
          }
        />
      </section>
      {children}
    </>
  );
}

export function placeholderMetadata(href: string) {
  return { title: findNavItem(href).label };
}
