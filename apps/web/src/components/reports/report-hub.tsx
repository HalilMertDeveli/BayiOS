"use client";

import Link from "next/link";
import { ArrowLeftRight, Bot, ChevronRight, ClipboardList, Lock, PackageMinus, ShoppingCart, Trophy, type LucideIcon } from "lucide-react";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { ordersOnly, reportAccess, reportHref, visibleReports, type ReportId } from "@/lib/reports/definitions";
import { useSession } from "@/lib/session/session-context";

const ICONS: Record<ReportId, LucideIcon> = {
  orders: ShoppingCart,
  "top-products": Trophy,
  "critical-stock": PackageMinus,
  "stock-movements": ArrowLeftRight,
  "picking-tasks": ClipboardList,
  "ai-usage": Bot,
};

/** W19 report hub: one card per report the role may open (GET /me permissions; the API checks again). */
export function ReportHub() {
  const { access } = useSession();
  const reports = visibleReports(access);
  if (reports.length === 0) {
    return (
      <>
        <PageHeader title="Raporlar" />
        <section aria-label="Raporlar durumu" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Raporları görme yetkiniz yok"
            description="Rolünüz raporlara erişime izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
          />
        </section>
      </>
    );
  }
  return (
    <>
      <PageHeader
        title="Raporlar"
        description="Stok, sipariş ve depo raporları. Her rapor CSV veya Excel olarak indirilebilir; dışa aktarma her pakette açıktır."
      />
      {ordersOnly(access) ? (
        <p className="text-body text-fg-muted">Rolünüz yalnızca sipariş raporlarını görebilir.</p>
      ) : null}
      <ul aria-label="Raporlar" className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {reports.map((report) => {
          const Icon = ICONS[report.id];
          const canExport = reportAccess(access, report).export;
          return (
            <li key={report.id}>
              <Link
                href={reportHref(report.id)}
                className="flex h-full items-start gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 hover:bg-sunken"
              >
                <Icon className="mt-0.5 size-5 shrink-0 text-brand-fg" aria-hidden="true" />
                <span className="flex flex-1 flex-col gap-1">
                  <span className="font-medium text-fg">{report.title}</span>
                  <span className="text-caption text-fg-muted">{report.description}</span>
                  <span className="text-caption text-fg-muted">
                    {report.periodic ? "Tarih aralığı: varsayılan son 30 gün" : "Anlık durum"}
                    {canExport ? " · CSV / Excel" : ""}
                  </span>
                </span>
                <ChevronRight className="mt-0.5 size-5 shrink-0 text-fg-muted" aria-hidden="true" />
              </Link>
            </li>
          );
        })}
      </ul>
    </>
  );
}
