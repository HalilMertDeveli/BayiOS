"use client";

import Link from "next/link";
import { ArrowLeft, Lock } from "@/components/ui/icons";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { ORDERS_HREF } from "@/lib/orders/api";
import { orderPermissions } from "@/lib/orders/permissions";
import { useSession } from "@/lib/session/session-context";
import { useResetAfterCreate } from "@/lib/use-reset-after-create";
import { OrderDraftEditor } from "./order-draft-editor";

/** /siparisler/yeni: manual draft order (orders.create). Saving creates the draft and opens its detail page. */
export function OrderCreatePage() {
  const { access } = useSession();
  const permissions = orderPermissions(access);
  const { generation, markCreated } = useResetAfterCreate();
  return (
    <>
      <Link href={ORDERS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
        <ArrowLeft className="size-4" aria-hidden="true" />
        Siparişler
      </Link>
      <PageHeader title="Yeni sipariş" description="Müşteriyi seçin, ürünleri okutun veya arayın. Taslak kaydedilince fiyatlar müşterinin fiyat listesinden çözülür." />
      {permissions.create ? (
        <OrderDraftEditor key={generation} loaded={null} permissions={permissions} onCreated={markCreated} />
      ) : (
        <section aria-label="Yeni sipariş durumu" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Sipariş oluşturma yetkiniz yok"
            description="Rolünüz manuel sipariş oluşturmaya izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
          />
        </section>
      )}
    </>
  );
}
